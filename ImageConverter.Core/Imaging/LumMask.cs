namespace ImageConverter.Core.Imaging;

/// <summary>
/// Luminosity mask in the style of the Referenzwerkzeug "Darks I/II/III" masks (Tony Kuyper): the inverted image itself serves
/// as the mask, so dark areas receive the full effect, midtones a share and bright areas none. The mask is pixel-accurate
/// (it follows every pine needle, no halo) and fades out smoothly.
/// </summary>
/// <remarks>
/// Weight = (1 − brightness / end)^p, and 0 above <c>end</c>. "Darks II" corresponds to p = 2 (the mask multiplied by
/// itself); the higher p, the steeper the mask, so only the darkest tones receive much. The brightness used for the mask is
/// 70 % that of the pixel and 30 % the smoothed brightness of its region (<see cref="LuminanceRegions"/>): a pure pixel
/// mask lifts the dark needles of a tree more than the sunlit ones, which would flatten the structure. Mixed pixels (half
/// needle, half sky) lie in the midtones and receive only a share, so the haze between the branches is lifted only weakly.
/// Tonal inversion is ruled out: with L + (k·L − L)·(1 − L/E)^p the curve stays monotonically increasing as long as
/// 1 + (k − 1)·min[(1 − u)^p − p·u·(1 − u)^(p−1)] ≥ 0. This holds for p ≥ 1.5 up to k = 3 (for p = 1 only up to k = 2;
/// for p &lt; 1 never, because the curve drops at the fade-out point). Hence the curve range p = 1.5 … 4 and a strength of
/// up to +200 %.
/// </remarks>
public static class LumMask
{
    /// <summary>Share of the pixel's own brightness in the mask brightness; the remainder comes from the region.</summary>
    public const float PixelShare = 0.7f;

    /// <summary>Converts the curve slider into the mask exponent.</summary>
    /// <param name="curve">Curve slider 0 … 100; values outside are clamped.</param>
    /// <returns>Exponent from 1.5 (soft) to 4 (steep), geometrically interpolated; 30 gives about 2 ("Darks II").</returns>
    public static float Power(double curve) => (float)(1.5 * Math.Pow(4 / 1.5, Math.Clamp(curve, 0, 100) / 100));

    /// <summary>Computes the mask weight for one pixel.</summary>
    /// <param name="pixel">Oklab L of the pixel.</param>
    /// <param name="area">Smoothed Oklab L of the region; without a region map, pass <paramref name="pixel"/>.</param>
    /// <param name="end">Fade-out point (Oklab L): from this brightness upwards the weight is 0.</param>
    /// <param name="power">Exponent p, see <see cref="Power"/>.</param>
    /// <returns>Weight 0 … 1.</returns>
    public static float Weight(float pixel, float area, float end, float power)
    {
        var l = PixelShare * pixel + (1 - PixelShare) * area;
        if (l >= end) return 0;
        return MathF.Pow(1 - Math.Max(0, l) / end, power);
    }
}
