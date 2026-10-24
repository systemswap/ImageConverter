using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Smooths steps in soft gradients (debanding) on floating-point pixels.
/// </summary>
/// <remarks>
/// <para>Method as in common video deband filters: per pixel, four
/// samples in a random direction and distance (a cross of two opposite pairs); the pixel is replaced by their mean,
/// but only where all four deviate by less than the threshold. In a gradient the neighbouring steps lie so close that
/// they are averaged: the staircase becomes a ramp with intermediate values that only the floating-point chain can
/// hold. Real edges and structure lie above the threshold and stay. Two rounds: first a small radius, then twice the
/// radius with half the threshold (as in common implementations); long flat gradients need the large radius, steeper ones the
/// small one.</para>
/// <para>Stricter than common implementations (where only the distance to the mean counts): every single sample must lie within
/// the threshold, and the effect fades out before the threshold. Otherwise fine, low-contrast structures in the
/// distance disappear, which the dehaze filter is meant to bring out afterwards.</para>
/// </remarks>
internal static class Deband
{
    /// <summary>Sample radius of the first round, relative to the longest edge (at 4000 px: 32 px; at least 2 px).</summary>
    private const double RadiusPerEdge = 0.008;

    /// <summary>Threshold at strength 1: 6 levels on the 8-bit scale.</summary>
    private const float MaxThreshold = 6f / 255;

    /// <summary>Debands an image. The input is not modified.</summary>
    /// <param name="px">Pixels, interleaved B, G, R, A, each 0…1.</param>
    /// <param name="width">Width in px.</param>
    /// <param name="height">Height in px.</param>
    /// <param name="strength">0 … 1; at 0 or below, <paramref name="px"/> itself is returned.</param>
    /// <param name="seed">A different random pattern per call, so that several passes average out.</param>
    /// <returns>A new buffer from <see cref="PixelPool"/>, or <paramref name="px"/> itself if the strength is 0.</returns>
    public static float[] Apply(float[] px, int width, int height, float strength, uint seed)
    {
        if (strength <= 0) return px;
        var radius = Math.Max(2, RadiusPerEdge * Math.Max(width, height));
        var current = px;
        for (var round = 1; round <= 2; round++)
        {
            var src = current;
            var dst = PixelPool.Rent(src.Length);   // Fully written
            var reach = (float)(radius * round);
            var bound = strength * MaxThreshold / round;
            var key = seed * 0x9E3779B9u + (uint)round * 0x85EBCA6Bu;
            var (offX, offY) = Offsets(reach);
            // Pattern shifted per call and round, so that the rounds do not coincide
            var shiftX = (int)(Pixels.Noise(key) * Pattern);
            var shiftY = (int)(Pixels.Noise(key ^ 0x5BD1E995u) * Pattern);
            // In tiles (TileWidth × TileRows): the samples lie up to 2 radii away; when whole image rows are processed
            // (128 KB each at 8000 px), their neighbourhood does not fit in the processor cache
            var tilesX = (width + TileWidth - 1) / TileWidth;
            var tilesY = (height + TileRows - 1) / TileRows;
            Parallel.For(0, tilesX * tilesY, ComputeLimits.Options, tile =>
            {
                Span<int> tap = stackalloc int[4];
                var tx0 = tile % tilesX * TileWidth;
                var tx1 = Math.Min(width, tx0 + TileWidth);
                var ty0 = tile / tilesX * TileRows;
                var ty1 = Math.Min(height, ty0 + TileRows);
                for (var y = ty0; y < ty1; y++)
                {
                    var row = ((y + shiftY) & (Pattern - 1)) * Pattern;
                    for (int x = tx0, p = y * width + tx0; x < tx1; x++, p++)
                    {
                        var i = p * 4;
                        var o = row + ((x + shiftX) & (Pattern - 1));
                        int dx = offX[o], dy = offY[o];
                        tap[0] = Index(x + dx, y + dy, width, height);
                        tap[1] = Index(x - dx, y - dy, width, height);
                        tap[2] = Index(x - dy, y + dx, width, height);
                        tap[3] = Index(x + dy, y - dx, width, height);

                        // Largest deviation of a sample; from the threshold on, the pixel stays (structure, edge),
                        // and the remaining samples are skipped
                        float s0 = src[i], s1 = src[i + 1], s2 = src[i + 2];
                        var worst = 0f;
                        for (var t = 0; t < 4 && worst < bound; t++)
                        {
                            var q = tap[t];
                            worst = MathF.Max(worst, MathF.Max(MathF.Abs(src[q] - s0),
                                MathF.Max(MathF.Abs(src[q + 1] - s1), MathF.Abs(src[q + 2] - s2))));
                        }
                        // Full effect up to half the threshold, none at the threshold
                        var w = worst >= bound ? 0 : Math.Clamp(2 - 2 * worst / bound, 0, 1);
                        if (w <= 0)
                        {
                            dst[i] = s0; dst[i + 1] = s1; dst[i + 2] = s2;
                        }
                        else
                        {
                            for (var c = 0; c < 3; c++)
                            {
                                var avg = (src[tap[0] + c] + src[tap[1] + c] + src[tap[2] + c] + src[tap[3] + c]) * 0.25f;
                                dst[i + c] = src[i + c] + (avg - src[i + c]) * w;
                            }
                        }
                        dst[i + 3] = src[i + 3];
                    }
                }
            });
            if (round == 2) PixelPool.Return(src);   // Intermediate result of the first round (owned here)
            current = dst;
        }
        return current;
    }

    /// <summary>Tile size: width in pixels, height in rows.</summary>
    private const int TileWidth = 256, TileRows = 64;

    /// <summary>Edge length of the random pattern of sample directions (power of two, used as a bit mask).</summary>
    private const int Pattern = 512;

    /// <summary>
    /// Random direction (half circle, because the samples form a cross) and distance (¼ … 1 of the reach) per cell of a
    /// pattern of <see cref="Pattern"/>² cells, computed in advance instead of sine and cosine per pixel.
    /// </summary>
    private static readonly Lazy<(float[] Cos, float[] Sin)> Directions = new(() =>
    {
        var cos = new float[Pattern * Pattern];
        var sin = new float[Pattern * Pattern];
        for (var p = 0; p < cos.Length; p++)
        {
            var angle = Pixels.Noise((uint)p * 2654435761u) * MathF.PI;
            var dist = 0.25f + 0.75f * Pixels.Noise((uint)p * 3u + 0x68E31DA4u);
            cos[p] = dist * MathF.Cos(angle);
            sin[p] = dist * MathF.Sin(angle);
        }
        return (cos, sin);
    });

    private static (int[] X, int[] Y) Offsets(float reach)
    {
        var (cos, sin) = Directions.Value;
        var x = new int[cos.Length];
        var y = new int[cos.Length];
        for (var p = 0; p < x.Length; p++)
        {
            x[p] = (int)MathF.Round(reach * cos[p]);
            y[p] = (int)MathF.Round(reach * sin[p]);
        }
        return (x, y);
    }

    private static int Index(int x, int y, int w, int h) =>
        (Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * 4;
}
