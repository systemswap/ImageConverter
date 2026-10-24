using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>Runs a ×4 tile model (Real-ESRGAN x4v3, UltraSharpV2) over a whole image and returns an <see cref="UpscaleResult"/>.</summary>
public static class UpscaleEngine
{
    /// <summary>Returns the number of tiles for an original of this size (tiled as <see cref="RunTiles"/> does).</summary>
    /// <param name="w">Width of the original in pixels.</param>
    /// <param name="h">Height of the original in pixels.</param>
    /// <param name="tile">Tile size of the model in pixels.</param>
    /// <returns>The number of tiles.</returns>
    public static int TileCount(int w, int h, int tile)
    {
        var core = tile - 2 * Math.Max(4, tile / 12);
        return ((w + core - 1) / core) * ((h + core - 1) / core);
    }

    /// <summary>Upscales an image ×4 with a tile model.</summary>
    /// <remarks>
    /// Tiles overlap; only the centre of each tile is kept (as in <see cref="AiEnhancer"/>). The tiles are processed one
    /// after another on the calling thread.
    /// </remarks>
    /// <param name="source">Original image; it is not modified.</param>
    /// <param name="model">Tile model with scale 4.</param>
    /// <param name="progress">Receives the completed fraction, 0…1, after each tile.</param>
    /// <param name="ct">Cancels the computation; checked before each tile and passed to the model.</param>
    /// <param name="done">Called with each finished area (in pixels of the original), on the computing thread.</param>
    /// <returns>The raw result; it is filled while the run proceeds.</returns>
    /// <exception cref="ArgumentException">The model does not scale by <see cref="UpscaleResult.Factor"/>.</exception>
    public static UpscaleResult RunTiles(MagickImage source, ITileModel model, IProgress<double>? progress, CancellationToken ct,
        Action<UpscaleResult, SourceRect>? done = null)
    {
        if (model.Scale != UpscaleResult.Factor)
            throw new ArgumentException($"Das Modell vergrößert ×{model.Scale}, gebraucht wird ×{UpscaleResult.Factor}.", nameof(model));
        int w = (int)source.Width, h = (int)source.Height;
        var channels = source.HasAlpha ? 4 : 3;
        var src = AiEnhancer.ReadPixels(source, channels == 4 ? "RGBA" : "RGB");
        var result = new UpscaleResult(source, src, channels);
        var raw = result.Raw;
        var ow = w * 4;

        var tile = model.TileSize;
        var pad = Math.Max(4, tile / 12);   // border that is discarded (as in AiEnhancer)
        var core = tile - 2 * pad;
        var tilesX = (w + core - 1) / core;
        var tilesY = (h + core - 1) / core;
        var input = new float[3 * tile * tile];
        var count = 0;
        for (var ty = 0; ty < tilesY; ty++)
        for (var tx = 0; tx < tilesX; tx++)
        {
            ct.ThrowIfCancellationRequested();
            int cx = tx * core, cy = ty * core;
            int cw = Math.Min(core, w - cx), ch = Math.Min(core, h - cy);
            for (var y = 0; y < tile; y++)
            {
                var sy = Math.Clamp(cy - pad + y, 0, h - 1);
                for (var x = 0; x < tile; x++)
                {
                    var si = ((long)sy * w + Math.Clamp(cx - pad + x, 0, w - 1)) * channels;
                    var di = y * tile + x;
                    input[di] = src[si] / 65535f;
                    input[tile * tile + di] = src[si + 1] / 65535f;
                    input[2 * tile * tile + di] = src[si + 2] / 65535f;
                }
            }
            var output = model.Run(input, tile, tile, ct);
            var os = tile * 4;
            var plane = os * os;
            Parallel.For(0, ch * 4, ComputeLimits.Options, y =>
            {
                var oy = cy * 4 + y;
                for (var x = 0; x < cw * 4; x++)
                {
                    var di = ((long)oy * ow + cx * 4 + x) * 3;
                    var si = (pad * 4 + y) * os + pad * 4 + x;
                    for (var c = 0; c < 3; c++)
                        raw[di + c] = (ushort)(Math.Clamp(output[c * plane + si], 0f, 1f) * 65535f + 0.5f);
                }
            });
            done?.Invoke(result, new SourceRect(cx, cy, cw, ch));
            progress?.Report(++count / (double)(tilesX * tilesY));
        }
        return result;
    }
}
