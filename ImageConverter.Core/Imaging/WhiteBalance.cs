using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Imaging;

/// <summary>Specifies the eyedropper: a spot that is to become grey, black or white.</summary>
/// <remarks><c>Neutral</c>: the spot becomes colourless, its brightness stays. <c>Black</c>: the spot becomes fully
/// black. <c>White</c>: the spot becomes fully white.</remarks>
public enum WbPoint { Neutral, Black, White }

/// <summary>
/// Colour measured by an eyedropper in linear light (0–1) and the number of clicks averaged in it.
/// </summary>
/// <remarks>Each further click with the same eyedropper is averaged in (<see cref="Add"/>), so that a single unfavourable
/// spot counts less.</remarks>
/// <param name="R">Red in linear light, 0–1.</param>
/// <param name="G">Green in linear light, 0–1.</param>
/// <param name="B">Blue in linear light, 0–1.</param>
/// <param name="Count">Number of clicks averaged in this sample.</param>
public readonly record struct WbSample(double R, double G, double B, int Count = 1)
{
    /// <summary>Combines this sample with another one.</summary>
    /// <param name="next">Further measurement.</param>
    /// <returns>The mean of both, weighted by the number of clicks, with the click counts added.</returns>
    public WbSample Add(WbSample next)
    {
        var n = Count + next.Count;
        return new WbSample((R * Count + next.R * next.Count) / n, (G * Count + next.G * next.Count) / n,
            (B * Count + next.B * next.Count) / n, n);
    }

    /// <summary>Converts the colour for display.</summary>
    /// <returns>The colour sRGB-encoded, 0–255 per channel.</returns>
    public (byte R, byte G, byte B) ToSrgb8() =>
        (WhiteBalance.Byte(R), WhiteBalance.Byte(G), WhiteBalance.Byte(B));
}

/// <summary>
/// White balance with up to three eyedroppers, each alone or combined.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Black and white set the tonal end points per colour channel (levels): the black spot
/// becomes fully black, the white spot fully white. This also removes a cast separately in shadows and highlights.</item>
/// <item>Neutral makes the grey spot colourless while its brightness stays. With white, this is done through the midtones
/// per channel (white stays white); without white, through a gain per channel, which physically corresponds to
/// converting to a different illuminant.</item>
/// </list>
/// Computed in linear light. Applied as the first step of the chain, that is, to the same image the measurement is
/// taken on.
/// </remarks>
public static class WhiteBalance
{
    /// <summary>Encoded level (0–1) from which a channel counts as clipped. Clipped pixels lack colour information and are
    /// excluded when measuring neutral and white.</summary>
    public const double ClippedLevel = 0.985;

    /// <summary>Minimum distance black → white per channel (linear); otherwise the curve would be unusably steep.</summary>
    private const double MinRange = 0.05;

    /// <summary>Computes the half edge length of the measuring square.</summary>
    /// <param name="width">Image width in px.</param>
    /// <param name="height">Image height in px.</param>
    /// <returns>Radius in px: one 300th of the shorter side, at least 4.</returns>
    public static int SampleRadius(int width, int height) => Math.Max(4, Math.Min(width, height) / 300);

    /// <summary>
    /// Measures the mean colour in a square of <see cref="SampleRadius"/> around a position.
    /// </summary>
    /// <param name="image">Image to measure; access is synchronised by locking on it.</param>
    /// <param name="x">Horizontal position in image pixels.</param>
    /// <param name="y">Vertical position in image pixels.</param>
    /// <param name="kind">Eyedropper; for <see cref="WbPoint.Neutral"/> and <see cref="WbPoint.White"/>, clipped pixels
    /// (<see cref="ClippedLevel"/>) are not counted.</param>
    /// <returns>The mean colour in linear light, or <see langword="null"/> if the position lies outside the image or all
    /// pixels are clipped.</returns>
    /// <exception cref="InvalidOperationException">The pixels could not be read.</exception>
    public static WbSample? Measure(MagickImage image, double x, double y, WbPoint kind)
    {
        int w, h;
        ushort[] data;
        int x0, y0, cw, ch;
        lock (image)
        {
            (w, h) = ((int)image.Width, (int)image.Height);
            if (x < 0 || y < 0 || x >= w || y >= h) return null;
            var r = SampleRadius(w, h);
            x0 = Math.Max(0, (int)x - r);
            y0 = Math.Max(0, (int)y - r);
            cw = Math.Min(w, (int)x + r + 1) - x0;
            ch = Math.Min(h, (int)y + r + 1) - y0;
            using var pixels = image.GetPixelsUnsafe();
            data = pixels.ToShortArray(x0, y0, (uint)cw, (uint)ch, "RGB")
                   ?? throw new InvalidOperationException("Pixel konnten nicht gelesen werden.");
        }

        double sr = 0, sg = 0, sb = 0;
        var count = 0;
        var skipClipped = kind != WbPoint.Black;
        for (var i = 0; i + 2 < data.Length; i += 3)
        {
            double r = data[i] / 65535.0, g = data[i + 1] / 65535.0, b = data[i + 2] / 65535.0;
            if (skipClipped && (r >= ClippedLevel || g >= ClippedLevel || b >= ClippedLevel)) continue;
            sr += ToLinear(r);
            sg += ToLinear(g);
            sb += ToLinear(b);
            count++;
        }
        return count == 0 ? null : new WbSample(sr / count, sg / count, sb / count);
    }

    /// <summary>Determines whether there is anything to do.</summary>
    /// <param name="neutral">Neutral measurement, or <see langword="null"/>.</param>
    /// <param name="black">Black measurement, or <see langword="null"/>.</param>
    /// <param name="white">White measurement, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if at least one measurement is set.</returns>
    public static bool IsActive(WbSample? neutral, WbSample? black, WbSample? white) =>
        neutral is not null || black is not null || white is not null;

    /// <summary>Curve per channel: tonal end points, then midtones (exponent) or gain.</summary>
    internal readonly record struct Channel(double Black, double White, double Gamma, double Gain)
    {
        public double Map(double linear)
        {
            var v = (linear - Black) / (White - Black);
            if (v <= 0) return 0;
            v = Gamma != 1 ? Math.Pow(Math.Min(v, 1), Gamma) : v;
            return Math.Clamp(v * Gain, 0, 1);
        }
    }

    /// <summary>The three channel curves (R, G, B) from the measurements.</summary>
    internal static Channel[] Plan(WbSample? neutral, WbSample? black, WbSample? white)
    {
        double[] Of(WbSample s) => [s.R, s.G, s.B];
        var b = black is { } bs ? Of(bs) : [0, 0, 0];
        var w = white is { } ws ? Of(ws) : [1, 1, 1];
        for (var c = 0; c < 3; c++) w[c] = Math.Max(w[c], b[c] + MinRange);

        var gamma = new double[] { 1, 1, 1 };
        var gain = new double[] { 1, 1, 1 };
        if (neutral is { } ns)
        {
            var n = Of(ns);
            var level = new double[3];
            for (var c = 0; c < 3; c++) level[c] = Math.Clamp((n[c] - b[c]) / (w[c] - b[c]), 1e-4, 1);
            // Target grey: brightness of the spot (ITU-R BT.709 weights, linear); it is only to lose its colour
            var target = Math.Clamp(0.2126 * level[0] + 0.7152 * level[1] + 0.0722 * level[2], 1e-4, 0.9999);
            for (var c = 0; c < 3; c++)
            {
                if (white is not null && level[c] < 0.9999)
                    gamma[c] = Math.Clamp(Math.Log(target) / Math.Log(level[c]), 0.2, 5);   // White stays white
                else
                    gain[c] = Math.Clamp(target / level[c], 0.2, 5);
            }
        }

        return [new(b[0], w[0], gamma[0], gain[0]), new(b[1], w[1], gamma[1], gain[1]), new(b[2], w[2], gamma[2], gain[2])];
    }

    /// <summary>Number of intervals of the lookup curve per channel (over the sRGB-encoded input, interpolated linearly in
    /// between).</summary>
    private const int TableSize = 4096;

    /// <summary>
    /// Applies the white balance to a pixel buffer.
    /// </summary>
    /// <param name="px">Pixels, interleaved B, G, R, A, sRGB-encoded 0–1; not modified.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="neutral">Neutral measurement, or <see langword="null"/>.</param>
    /// <param name="black">Black measurement, or <see langword="null"/>.</param>
    /// <param name="white">White measurement, or <see langword="null"/>.</param>
    /// <returns>A new buffer from the <see cref="PixelPool"/>, owned by the caller. Alpha stays unchanged.</returns>
    public static float[] Apply(float[] px, int width, int height, WbSample? neutral, WbSample? black, WbSample? white)
    {
        var plan = Plan(neutral, black, white);
        // Curve per channel: encoded input → encoded output (order in the buffer: B, G, R)
        var tables = new float[3][];
        for (var c = 0; c < 3; c++)
        {
            var channel = plan[2 - c];   // Buffer B, G, R ↔ plan R, G, B
            var t = new float[TableSize + 1];
            for (var i = 0; i <= TableSize; i++)
                t[i] = (float)ToSrgb(channel.Map(ToLinear(i / (double)TableSize)));
            tables[c] = t;
        }

        var result = PixelPool.Rent(px.Length);
        var pixels = width * height;
        Parallel.For(0, height, ComputeLimits.Options, y =>
        {
            for (int i = y * width * 4, end = Math.Min(i + width * 4, pixels * 4); i < end; i += 4)
            {
                result[i] = Lookup(tables[0], px[i]);
                result[i + 1] = Lookup(tables[1], px[i + 1]);
                result[i + 2] = Lookup(tables[2], px[i + 2]);
                result[i + 3] = px[i + 3];
            }
        });
        return result;
    }

    private static float Lookup(float[] table, float v)
    {
        var f = Math.Clamp(v, 0f, 1f) * TableSize;
        var i = (int)f;
        if (i >= TableSize) return table[TableSize];
        return table[i] + (table[i + 1] - table[i]) * (f - i);
    }

    /// <summary>Converts an sRGB-encoded value to linear light (IEC 61966-2-1 transfer function).</summary>
    /// <param name="v">Encoded value, 0–1.</param>
    /// <returns>Linear value, 0–1.</returns>
    public static double ToLinear(double v) =>
        v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

    /// <summary>Converts a linear value to sRGB encoding (IEC 61966-2-1 transfer function).</summary>
    /// <param name="v">Linear value, 0–1.</param>
    /// <returns>Encoded value, 0–1.</returns>
    public static double ToSrgb(double v) =>
        v <= 0.0031308 ? v * 12.92 : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055;

    internal static byte Byte(double linear) => (byte)Math.Clamp(Math.Round(ToSrgb(Math.Clamp(linear, 0, 1)) * 255), 0, 255);
}
