using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// An image model that processes one tile at a time: planar RGB in, planar RGB at
/// <see cref="Scale"/> times the size out.
/// </summary>
/// <remarks>
/// Both input and output are laid out as 3 × height × width (channel planes R, G, B) with values
/// in the range 0…1.
/// </remarks>
public interface ITileModel
{
    /// <summary>Gets the integer magnification of the model (output edge = input edge × scale).</summary>
    int Scale { get; }

    /// <summary>Gets the edge length of an input tile in pixels.</summary>
    int TileSize { get; }

    /// <summary>Runs the model on one tile.</summary>
    /// <param name="input">Planar RGB, 3 × <paramref name="height"/> × <paramref name="width"/>, values 0…1.</param>
    /// <param name="width">Tile width in pixels.</param>
    /// <param name="height">Tile height in pixels.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <returns>Planar RGB, 3 × (height × <see cref="Scale"/>) × (width × <see cref="Scale"/>), values nominally 0…1.</returns>
    float[] Run(float[] input, int width, int height, CancellationToken ct);
}

/// <summary>
/// Runs a whole image through an <see cref="ITileModel"/>.
/// </summary>
/// <remarks>
/// Tiles overlap and only the centre of each tile is kept, so no seams are visible. For output
/// sizes below the model scale (for example "keep size"), the enlarged model output is averaged
/// block by block (supersampling). This happens exactly per tile, so the full-scale model output
/// of the whole image is never held in memory.
/// </remarks>
public static class AiEnhancer
{
    /// <summary>Largest permitted output size in pixels; limits memory use and computing time.</summary>
    public const long MaxOutputPixels = 80_000_000;

    /// <summary>Returns the output factors that are possible for an image (1 = keep size).</summary>
    /// <param name="modelScale">Magnification of the model (<see cref="ITileModel.Scale"/>).</param>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <returns>
    /// Those of 1, 2, 4 and 8 that do not exceed <paramref name="modelScale"/>, divide it evenly and keep
    /// the output within <see cref="MaxOutputPixels"/>, in ascending order.
    /// </returns>
    public static IEnumerable<int> PossibleFactors(int modelScale, int width, int height) =>
        new[] { 1, 2, 4, 8 }.Where(f => f <= modelScale && modelScale % f == 0
                                        && (long)width * f * height * f <= MaxOutputPixels);

    /// <summary>Enhances an image with a tile model and blends the result with the original.</summary>
    /// <param name="source">Image to enhance; it is not modified.</param>
    /// <param name="model">Tile model to apply.</param>
    /// <param name="factor">Output size: 1 = same as the original, otherwise a multiple that divides the model scale.</param>
    /// <param name="strength">0…1: blend with the original (1 = model only).</param>
    /// <param name="progress">Receives the completed fraction, 0…1.</param>
    /// <param name="ct">Cancels the computation.</param>
    /// <param name="preview">Receives intermediate frames: finished tiles enhanced, the rest still from the original.</param>
    /// <param name="previewMaxEdge">Longest edge of the intermediate frames in pixels.</param>
    /// <returns>A new image owned by the caller, with the profiles and density of <paramref name="source"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is less than 1 or does not divide the model scale.</exception>
    public static MagickImage Enhance(MagickImage source, ITileModel model, int factor, double strength,
        IProgress<double>? progress, CancellationToken ct,
        Action<AiPreviewFrame>? preview = null, int previewMaxEdge = 2048) =>
        Run(source, model, factor, progress, ct, preview, previewMaxEdge).Compose(new AiBlend(strength));

    /// <summary>
    /// Applies the model at full strength to the whole image without blending it with the original.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="EnhanceRun"/> holds both the model result and the original at the
    /// target size, so the strength can be changed live afterwards without running the model again.
    /// The tiles are processed one after another on the calling thread; the call blocks until all
    /// tiles are done. <see cref="MaxOutputPixels"/> is not checked here; callers choose the factor
    /// from <see cref="PossibleFactors"/>.
    /// </remarks>
    /// <param name="source">Image to enhance; it is not modified.</param>
    /// <param name="model">Tile model to apply.</param>
    /// <param name="factor">Output size: 1 = same as the original, otherwise a multiple that divides the model scale.</param>
    /// <param name="progress">Receives the completed fraction, 0…1, after each tile.</param>
    /// <param name="ct">Cancels the computation; checked before each tile and passed to the model.</param>
    /// <param name="preview">
    /// Receives intermediate frames at most every 400 ms: finished tiles enhanced, the rest from the
    /// original, shown paler. No frame is sent after the last tile.
    /// </param>
    /// <param name="previewMaxEdge">Longest edge of the intermediate frames in pixels.</param>
    /// <returns>The unblended result at the target size.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is less than 1 or does not divide the model scale.</exception>
    public static EnhanceRun Run(MagickImage source, ITileModel model, int factor,
        IProgress<double>? progress, CancellationToken ct,
        Action<AiPreviewFrame>? preview = null, int previewMaxEdge = 2048)
    {
        var scale = model.Scale;
        if (factor < 1 || scale % factor != 0)
            throw new ArgumentOutOfRangeException(nameof(factor), $"Vergrößerung {factor} passt nicht zum Modell (×{scale}).");

        int w = (int)source.Width, h = (int)source.Height;
        var hasAlpha = source.HasAlpha;
        var channels = hasAlpha ? 4 : 3;
        var mapping = hasAlpha ? "RGBA" : "RGB";

        var src = ReadPixels(source, mapping);
        int ow = w * factor, oh = h * factor;

        // Reference image at the target size, used for blending (strength) and for the alpha channel.
        // It is always created because the strength is chosen only afterwards.
        ushort[] baseline;
        if (factor == 1) baseline = src;
        else
        {
            using var up = ImageOps.CloneOf(source);
            up.FilterType = FilterType.Lanczos;
            up.Resize(new MagickGeometry((uint)ow, (uint)oh) { IgnoreAspectRatio = true });
            baseline = ReadPixels(up, mapping);
        }

        var result = new ushort[(long)ow * oh * channels];
        var tile = model.TileSize;
        var pad = Math.Max(4, tile / 12);   // border that is discarded
        var core = tile - 2 * pad;
        var block = scale / factor;          // edge length in model pixels of the block that forms one output pixel
        var input = new float[3 * tile * tile];

        var tilesX = (w + core - 1) / core;
        var tilesY = (h + core - 1) / core;
        var done = 0;
        var sink = new AiPreviewSink(preview, previewMaxEdge);

        for (var ty = 0; ty < tilesY; ty++)
        for (var tx = 0; tx < tilesX; tx++)
        {
            ct.ThrowIfCancellationRequested();
            int cx = tx * core, cy = ty * core;
            int cw = Math.Min(core, w - cx), ch = Math.Min(core, h - cy);

            // Input including the border; outside the image the edge pixels are repeated
            for (var y = 0; y < tile; y++)
            {
                var sy = Math.Clamp(cy - pad + y, 0, h - 1);
                for (var x = 0; x < tile; x++)
                {
                    var sx = Math.Clamp(cx - pad + x, 0, w - 1);
                    var si = ((long)sy * w + sx) * channels;
                    var di = y * tile + x;
                    input[di] = src[si] / 65535f;
                    input[tile * tile + di] = src[si + 1] / 65535f;
                    input[2 * tile * tile + di] = src[si + 2] / 65535f;
                }
            }

            var output = model.Run(input, tile, tile, ct);
            var os = tile * scale;               // edge length of the model output
            var plane = (long)os * os;
            var norm = 1f / (block * block);

            // Take over the core, averaged down to the target size
            for (var y = 0; y < ch * factor; y++)
            {
                var oy = cy * factor + y;
                for (var x = 0; x < cw * factor; x++)
                {
                    var ox = cx * factor + x;
                    var di = ((long)oy * ow + ox) * channels;

                    for (var c = 0; c < 3; c++)
                    {
                        float sum = 0;
                        var my0 = pad * scale + y * block;
                        var mx0 = pad * scale + x * block;
                        for (var by = 0; by < block; by++)
                        {
                            var row = c * plane + (long)(my0 + by) * os + mx0;
                            for (var bx = 0; bx < block; bx++) sum += output[row + bx];
                        }

                        var v = Math.Clamp(sum * norm, 0f, 1f) * 65535f;
                        result[di + c] = (ushort)(v + 0.5f);
                    }

                    if (hasAlpha) result[di + 3] = baseline[di + 3];
                }
            }

            progress?.Report(++done / (double)(tilesX * tilesY));

            if (sink.IsDue && done < tilesX * tilesY)
            {
                // Tiles run row by row: every tile with an index below "done" is finished
                var finished = done;
                sink.Emit(ow, oh, (x, y, rgba) =>
                {
                    int sx = x / factor, sy = y / factor;
                    var isDone = sy / core * tilesX + sx / core < finished;
                    var i = isDone ? ((long)y * ow + x) * channels : ((long)sy * w + sx) * channels;
                    var data = isDone ? result : src;
                    // Not yet computed: shown slightly paler so the progress of the rebuild is visible
                    var fade = isDone ? 1f : 0.6f;
                    var lift = isDone ? 0f : 0.4f;
                    rgba[0] = data[i] / 65535f * fade + lift;
                    rgba[1] = data[i + 1] / 65535f * fade + lift;
                    rgba[2] = data[i + 2] / 65535f * fade + lift;
                    rgba[3] = hasAlpha ? src[((long)sy * w + sx) * channels + 3] / 65535f : 1f;
                });
            }
        }

        return new EnhanceRun(source, result, baseline, ow, oh, mapping, channels);
    }

    internal static ushort[] ReadPixels(MagickImage image, string mapping)
    {
        lock (image)
        {
            using var pixels = image.GetPixelsUnsafe();
            return pixels.ToShortArray(mapping)
                   ?? throw new InvalidOperationException(Tr.T("Pixel konnten nicht gelesen werden."));
        }
    }

    /// <summary>
    /// Reduces pixel data to preview size exactly like the normal preview; returns interleaved RGB, 0…1.
    /// </summary>
    internal static float[] Shrink(ushort[] data, int width, int height, string mapping, AiPreviewSize size)
    {
        using var full = WritePixels(data, width, height, mapping);
        using var small = ImageOps.Resize(full, size.Width, size.Height, size.Filter, size.GammaCorrect);
        var px = ReadPixels(small, "RGB");
        var result = new float[px.Length];
        for (var i = 0; i < px.Length; i++) result[i] = px[i] / 65535f;
        return result;
    }

    internal static MagickImage WritePixels(ushort[] data, int width, int height, string mapping)
    {
        // Read directly from the 16-bit array (Q16: Quantum = ushort). Going through a byte copy of the same
        // size would cost an extra 1.15 GB for the ×4 result of a 12-MP photo.
        var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings((uint)width, (uint)height, StorageType.Quantum, mapping));
        return image;
    }
}
