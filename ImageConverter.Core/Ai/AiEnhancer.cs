using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Core.Ai;

/// <summary>
/// Ein Bildmodell, das Kacheln verarbeitet: RGB planar (3 × h × w, Werte 0…1) hinein,
/// dasselbe in <see cref="Scale"/>-facher Größe heraus.
/// </summary>
public interface ITileModel
{
    int Scale { get; }

    /// <summary>Kantenlänge einer Kachel (Eingabe).</summary>
    int TileSize { get; }

    float[] Run(float[] input, int width, int height, CancellationToken ct);
}

/// <summary>
/// Ganzes Bild durch ein Kachelmodell. Kacheln überlappen; von jeder Kachel wird nur
/// die Mitte übernommen — so gibt es keine sichtbaren Nähte. Für „Größe behalten“
/// wird das vergrößerte Ergebnis blockweise gemittelt (Supersampling): exakt pro
/// Kachel, ohne dass je das ganze 4×-Bild im Speicher liegt.
/// </summary>
public static class AiEnhancer
{
    /// <summary>Mehr Pixel als das wird das Ergebnis nicht (Speicher, Zeit).</summary>
    public const long MaxOutputPixels = 80_000_000;

    /// <summary>Welche Vergrößerungen für ein Bild möglich sind (1 = Größe behalten).</summary>
    public static IEnumerable<int> PossibleFactors(int modelScale, int width, int height) =>
        new[] { 1, 2, 4, 8 }.Where(f => f <= modelScale && modelScale % f == 0
                                        && (long)width * f * height * f <= MaxOutputPixels);

    /// <param name="factor">Ausgabegröße: 1 = wie das Original, sonst Vielfaches (Teiler der Modellskalierung).</param>
    /// <param name="strength">0…1: Mischung mit dem Original (1 = nur Modell).</param>
    /// <param name="progress">Anteil 0…1.</param>
    /// <param name="preview">Zwischenstände: fertige Kacheln neu, der Rest noch alt.</param>
    public static MagickImage Enhance(MagickImage source, ITileModel model, int factor, double strength,
        IProgress<double>? progress, CancellationToken ct,
        Action<AiPreviewFrame>? preview = null, int previewMaxEdge = 2048) =>
        Run(source, model, factor, progress, ct, preview, previewMaxEdge).Compose(new AiBlend(strength));

    /// <summary>
    /// Das Modell mit voller Wirkung über das ganze Bild, noch nicht mit dem Original gemischt
    /// (<see cref="EnhanceRun"/>) — die Stärke lässt sich danach live ändern, ohne neu zu rechnen.
    /// </summary>
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

        // Vergleichsbild in Zielgröße: für die Mischung (Stärke) und den Alphakanal — immer, die Stärke
        // wird erst danach gewählt
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
        var pad = Math.Max(4, tile / 12);   // Rand, der verworfen wird
        var core = tile - 2 * pad;
        var block = scale / factor;          // so viele Modellpixel ergeben einen Ausgabepixel
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

            // Eingabe mit Rand; außerhalb des Bildes Randpixel wiederholen
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
            var os = tile * scale;               // Kantenlänge der Modellausgabe
            var plane = (long)os * os;
            var norm = 1f / (block * block);

            // Kern übernehmen, auf Zielgröße gemittelt
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
                // Kacheln laufen zeilenweise: fertig ist alles vor Kachel Nr. „done“
                var finished = done;
                sink.Emit(ow, oh, (x, y, rgba) =>
                {
                    int sx = x / factor, sy = y / factor;
                    var isDone = sy / core * tilesX + sx / core < finished;
                    var i = isDone ? ((long)y * ow + x) * channels : ((long)sy * w + sx) * channels;
                    var data = isDone ? result : src;
                    // Noch nicht berechnet: etwas blass — so sieht man, wie das Bild neu aufgebaut wird
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

    /// <summary>Auf Vorschaugröße verkleinern, genau wie die normale Vorschau (RGB verschachtelt, 0…1).</summary>
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
        // Direkt aus dem 16-Bit-Feld (Q16: Quantum = ushort) — vorher über eine Bytekopie gleicher Größe (beim ×4-Ergebnis
        // eines 12-MP-Fotos 1,15 GB zusätzlich)
        var image = new MagickImage();
        image.ReadPixels(data, new PixelReadSettings((uint)width, (uint)height, StorageType.Quantum, mapping));
        return image;
    }
}
