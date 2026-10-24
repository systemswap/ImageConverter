using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>Rectangle in pixels of the original (the result is <see cref="UpscaleResult.Factor"/> times as large).</summary>
/// <param name="X">Left edge.</param>
/// <param name="Y">Top edge.</param>
/// <param name="Width">Width.</param>
/// <param name="Height">Height.</param>
public readonly record struct SourceRect(int X, int Y, int Width, int Height)
{
    /// <summary>Gets whether the rectangle has no area.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// Result of an upscaler at four times the size, raw (as the model delivers it), together with the original.
/// </summary>
/// <remarks>
/// Fidelity and details are applied only when composing (<see cref="Compose"/>, <see cref="ComposeRegion"/>), so the
/// controls can be changed without running the model again. An alpha channel never comes from the model but is
/// enlarged from the original with Lanczos. During the run the result already exists and fills up; the run reports
/// finished areas one by one.
/// </remarks>
public sealed class UpscaleResult
{
    /// <summary>Magnification of every upscaler.</summary>
    public const int Factor = 4;

    private readonly ushort[] _source;   // original, interleaved (RGB or RGBA)
    private readonly int _channels;
    private readonly MagickImage _meta;  // profiles and density (1 × 1 pixel)

    // Colour matching per channel (PiSA-SR: AdaIN), applied when reading the raw result: v · gain + offset
    private readonly float[] _gain = [1, 1, 1];
    private readonly float[] _offset = [0, 0, 0];

    internal UpscaleResult(MagickImage source, ushort[] sourcePixels, int channels)
    {
        (_source, _channels) = (sourcePixels, channels);
        SourceWidth = (int)source.Width;
        SourceHeight = (int)source.Height;
        Raw = new ushort[(long)Width * Height * 3];
        _meta = new MagickImage(MagickColors.Black, 1, 1);
        lock (source)
        {
            _meta.Density = source.Density;
            ImageExporter.CopyProfiles(source, _meta);
        }
    }

    /// <summary>Gets the width of the original in pixels.</summary>
    public int SourceWidth { get; }

    /// <summary>Gets the height of the original in pixels.</summary>
    public int SourceHeight { get; }

    /// <summary>Gets the width of the result in pixels.</summary>
    public int Width => SourceWidth * Factor;

    /// <summary>Gets the height of the result in pixels.</summary>
    public int Height => SourceHeight * Factor;

    /// <summary>Gets whether the original, and therefore the composed result, has an alpha channel.</summary>
    public bool HasAlpha => _channels == 4;

    /// <summary>Raw model result (RGB, ×4, 16 bit), filled by the run.</summary>
    internal ushort[] Raw { get; }

    internal void SetColorFix(int channel, float gain, float offset) => (_gain[channel], _offset[channel]) = (gain, offset);

    /// <summary>
    /// Returns the approximate memory need for an original of this size: raw result (RGB, 16 bit), composed result
    /// (up to RGBA) and the image made from it (ImageMagick, 16 bit), each at four times the edge length.
    /// </summary>
    /// <param name="sourceWidth">Width of the original in pixels.</param>
    /// <param name="sourceHeight">Height of the original in pixels.</param>
    /// <returns>The estimate in bytes.</returns>
    public static long MemoryNeed(int sourceWidth, int sourceHeight) => (long)sourceWidth * sourceHeight * Factor * Factor * (6 + 8 + 8);

    /// <summary>Composes the finished image at four times the size.</summary>
    /// <param name="fidelity">0 … 1: fidelity constraint (1 = the result, reduced, gives the original again).</param>
    /// <param name="details">0 … 1: share of the AI against plain enlargement (Lanczos); 1 = AI only.</param>
    /// <param name="ct">Cancels the composition.</param>
    /// <returns>
    /// A new image owned by the caller, with the profiles of the original and the density multiplied by
    /// <see cref="Factor"/> (the print size stays the same).
    /// </returns>
    public MagickImage Compose(double fidelity, double details, CancellationToken ct = default)
    {
        // In bands directly into the image instead of a full-size array first (1.15 GB at 192 MP) and a copy afterwards. The
        // bands are a multiple of the ComposeRegion tiles (128 source rows) and read the margin for the fidelity constraint
        // from the whole result (known), so every pixel is computed exactly as in one piece.
        // With an alpha channel in one piece: Extent combines fully transparent pixels with the background (the colour
        // becomes 0). See docs/FEHLERGEDAECHTNIS.md, No. 75.
        var map = HasAlpha ? "RGBA" : "RGB";
        var all = new SourceRect(0, 0, SourceWidth, SourceHeight);
        var first = HasAlpha ? SourceHeight : Math.Min(BandRows, SourceHeight);
        var image = AiEnhancer.WritePixels(ComposeRegion(all with { Height = first }, fidelity, details, ct, all), Width,
            first * Factor, map);
        if (first < SourceHeight)
        {
            // Extend to full height (empty at the bottom), then insert the remaining bands
            image.Extent((uint)Width, (uint)Height, Gravity.North, HasAlpha ? MagickColors.Transparent : MagickColors.Black);
            using var pixels = image.GetPixelsUnsafe();
            for (var y = first; y < SourceHeight; y += BandRows)
            {
                ct.ThrowIfCancellationRequested();
                var rows = Math.Min(BandRows, SourceHeight - y);
                var band = ComposeRegion(new SourceRect(0, y, SourceWidth, rows), fidelity, details, ct, all);
                pixels.SetArea(0, y * Factor, (uint)Width, (uint)(rows * Factor), band);
            }
        }
        image.Density = new Density(_meta.Density.X * Factor, _meta.Density.Y * Factor, _meta.Density.Units);
        ImageExporter.CopyProfiles(_meta, image);
        return image;
    }

    /// <summary>Source rows per band when composing; a multiple of the tiles (128) of <see cref="ComposeRegion"/>.</summary>
    private const int BandRows = 512;

    /// <summary>
    /// Composes one region.
    /// </summary>
    /// <remarks>
    /// For the fidelity constraint a margin around the region is read as well, but at most up to <paramref name="known"/>:
    /// during the run the raw result next to it is still empty, which would cause bright lines at the tile edges.
    /// See docs/FEHLERGEDAECHTNIS.md, No. 61. Tiles of 128 source pixels are computed in parallel.
    /// </remarks>
    /// <param name="region">Region in pixels of the original.</param>
    /// <param name="fidelity">0 … 1: fidelity constraint.</param>
    /// <param name="details">0 … 1: share of the AI against plain enlargement (Lanczos).</param>
    /// <param name="ct">Cancels the composition.</param>
    /// <param name="known">Area whose raw result is finished; null = the whole image.</param>
    /// <returns>Pixels (interleaved RGB or RGBA, 16 bit) for the region ×4, row by row.</returns>
    public ushort[] ComposeRegion(SourceRect region, double fidelity, double details, CancellationToken ct = default,
        SourceRect? known = null)
    {
        var k = known ?? new SourceRect(0, 0, SourceWidth, SourceHeight);
        int sw = SourceWidth, sh = SourceHeight, ow = Width, ch = _channels;
        var f = (float)Math.Clamp(fidelity, 0, 1);
        var d = (float)Math.Clamp(details, 0, 1);
        int regionW = region.Width * Factor, regionH = region.Height * Factor;
        var output = new ushort[(long)regionW * regionH * ch];
        const int core = 128, margin = 16;   // in source pixels; the back projection (3 rounds) reaches about 15 pixels
        var tilesX = (region.Width + core - 1) / core;
        var tilesY = (region.Height + core - 1) / core;
        float Src(int c, int x, int y) =>
            _source[((long)Math.Clamp(y, 0, sh - 1) * sw + Math.Clamp(x, 0, sw - 1)) * ch + c] / 65535f;

        Parallel.For(0, tilesX * tilesY, new ParallelOptions { MaxDegreeOfParallelism = ComputeLimits.Options.MaxDegreeOfParallelism, CancellationToken = ct }, t =>
        {
            int cx = region.X + t % tilesX * core, cy = region.Y + t / tilesX * core;
            int cw = Math.Min(core, region.X + region.Width - cx), chh = Math.Min(core, region.Y + region.Height - cy);
            int rx = Math.Max(k.X, cx - margin), ry = Math.Max(k.Y, cy - margin);
            int rw = Math.Min(k.X + k.Width, cx + cw + margin) - rx, rh = Math.Min(k.Y + k.Height, cy + chh + margin) - ry;
            int bw = rw * Factor, bh = rh * Factor;

            var raw = new float[3 * bw * bh];
            for (var c = 0; c < 3; c++)
            {
                var (g, o) = (_gain[c] / 65535f, _offset[c]);
                for (var y = 0; y < bh; y++)
                {
                    var row = ((long)(ry * Factor + y) * ow + rx * Factor) * 3 + c;
                    var dst = c * bw * bh + y * bw;
                    for (var x = 0; x < bw; x++) raw[dst + x] = Raw[row + x * 3] * g + o;
                }
            }

            var e = raw;
            if (f > 0)
            {
                var s = new float[3 * rw * rh];
                for (var c = 0; c < 3; c++)
                for (var y = 0; y < rh; y++)
                for (var x = 0; x < rw; x++) s[c * rw * rh + y * rw + x] = Src(c, rx + x, ry + y);
                var bp = (float[])raw.Clone();
                Resample4.BackProject(bp, s, rw, rh);
                if (f >= 1) e = bp;
                else for (var i = 0; i < raw.Length; i++) raw[i] += f * (bp[i] - raw[i]);
            }
            float[]? plain = d < 1 || ch == 4
                ? Resample4.Up(Src, ch, cx * Factor, cy * Factor, cw * Factor, chh * Factor, Resample4.Kernel.Lanczos)
                : null;

            int ox = (cx - rx) * Factor, oy = (cy - ry) * Factor, pw = cw * Factor, ph = chh * Factor;
            int dx0 = (cx - region.X) * Factor, dy0 = (cy - region.Y) * Factor;
            for (var y = 0; y < ph; y++)
            for (var x = 0; x < pw; x++)
            {
                var di = ((long)(dy0 + y) * regionW + dx0 + x) * ch;
                var si = (oy + y) * bw + ox + x;
                var pi = y * pw + x;
                for (var c = 0; c < 3; c++)
                {
                    var v = e[c * bw * bh + si];
                    if (d < 1) v = plain![c * pw * ph + pi] + d * (v - plain[c * pw * ph + pi]);
                    output[di + c] = (ushort)(Math.Clamp(v, 0f, 1f) * 65535f + 0.5f);
                }
                if (ch == 4) output[di + 3] = (ushort)(Math.Clamp(plain![3 * pw * ph + pi], 0f, 1f) * 65535f + 0.5f);
            }
        });
        return output;
    }
}
