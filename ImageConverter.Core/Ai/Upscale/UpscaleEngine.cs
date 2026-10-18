using ImageMagick;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>Ein ×4-Kachelmodell (Real-ESRGAN x4v3, UltraSharpV2) über ein ganzes Bild, als <see cref="UpscaleResult"/>.</summary>
public static class UpscaleEngine
{
    /// <summary>Zahl der Kacheln für ein Original dieser Größe (wie <see cref="RunTiles"/> kachelt).</summary>
    public static int TileCount(int w, int h, int tile)
    {
        var core = tile - 2 * Math.Max(4, tile / 12);
        return ((w + core - 1) / core) * ((h + core - 1) / core);
    }

    /// <param name="done">Fertige Fläche (in Pixeln des Originals) — aus dem Rechen-Thread aufgerufen.</param>
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
        var pad = Math.Max(4, tile / 12);   // Rand, der verworfen wird (wie AiEnhancer)
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
