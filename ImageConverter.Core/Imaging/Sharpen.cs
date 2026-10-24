using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Sharpens images with an unsharp mask, fast enough for a live preview while a slider is dragged.
/// </summary>
/// <remarks>
/// Preview and export use the same function, so that the export matches what the preview showed.
/// The blur is <see cref="Blur.Gauss"/> (Gaussian, approximated by three box filter passes for larger radii).
/// Only B, G and R are sharpened; alpha stays unchanged.
/// </remarks>
public static class Sharpen
{
    /// <summary>Applies an unsharp mask to 8-bit BGRA pixels.</summary>
    /// <param name="bgra">Pixels, 4 bytes per pixel in the order B, G, R, A; not modified.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="amount">Strength; 0 = off, 1 = amplify edges by 100 %. Values up to 0.001 count as off.</param>
    /// <param name="sigma">Blur radius in px (Gaussian sigma); values up to 0.05 count as off.</param>
    /// <param name="threshold">Minimum difference in tone levels (0–255), so that flat areas and noise are not
    /// sharpened as well.</param>
    /// <returns>A new array with the sharpened pixels; an unchanged copy if sharpening is off.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The length of <paramref name="bgra"/> is not
    /// <paramref name="width"/> · <paramref name="height"/> · 4.</exception>
    public static byte[] UnsharpMask(byte[] bgra, int width, int height,
        double amount, double sigma, double threshold = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bgra.Length, width * height * 4, nameof(bgra));

        var result = (byte[])bgra.Clone();
        if (amount <= 0.001 || sigma <= 0.05) return result;

        var pixels = width * height;
        var blur = new float[pixels * 3];

        // B, G, R into one floating-point plane; alpha is not sharpened
        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var p = row + x;
                blur[p * 3] = bgra[p * 4];
                blur[p * 3 + 1] = bgra[p * 4 + 1];
                blur[p * 3 + 2] = bgra[p * 4 + 2];
            }
        });

        Blur.Gauss(blur, width, height, sigma, channels: 3);

        var amt = (float)amount;
        var thr = (float)threshold;
        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var p = row + x;
                for (var c = 0; c < 3; c++)
                {
                    float original = bgra[p * 4 + c];
                    var diff = original - blur[p * 3 + c];
                    if (MathF.Abs(diff) < thr) continue;
                    var v = original + amt * diff;
                    result[p * 4 + c] = (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5f);
                }
            }
        });

        return result;
    }

    /// <summary>
    /// Applies an unsharp mask to the 32-bit float buffer (<see cref="Pixels"/>) without rounding; used by preview,
    /// export and the dehaze passes.
    /// </summary>
    /// <param name="px">Pixels, interleaved B, G, R, A, each 0…1; not modified.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="amount">Strength; 0 = off, 1 = amplify edges by 100 %. Values up to 0.001 count as off.</param>
    /// <param name="sigma">Blur radius in px (Gaussian sigma); values up to 0.05 count as off.</param>
    /// <param name="threshold">Minimum difference in 8-bit tone levels (0–255, converted to 0…1 internally).</param>
    /// <returns>A new buffer rented from <see cref="PixelPool"/>, owned by the caller; an unchanged copy if sharpening
    /// is off. Results are clamped to 0…1.</returns>
    public static float[] UnsharpMask(float[] px, int width, int height,
        double amount, double sigma, double threshold = 2)
    {
        var result = PixelPool.Rent(px.Length);
        Array.Copy(px, result, px.Length);
        if (amount <= 0.001 || sigma <= 0.05) return result;

        var pixels = width * height;
        var blur = PixelPool.Rent(pixels * 3);
        Parallel.For(0, pixels, ComputeLimits.Options, p =>
        {
            blur[p * 3] = px[p * 4];
            blur[p * 3 + 1] = px[p * 4 + 1];
            blur[p * 3 + 2] = px[p * 4 + 2];
        });

        Blur.Gauss(blur, width, height, sigma, channels: 3);

        var amt = (float)amount;
        var thr = (float)threshold / 255;
        Parallel.For(0, pixels, ComputeLimits.Options, p =>
        {
            for (var c = 0; c < 3; c++)
            {
                var original = px[p * 4 + c];
                var diff = original - blur[p * 3 + c];
                if (MathF.Abs(diff) < thr) continue;
                result[p * 4 + c] = Math.Clamp(original + amt * diff, 0, 1);
            }
        });

        PixelPool.Return(blur);
        return result;
    }
}
