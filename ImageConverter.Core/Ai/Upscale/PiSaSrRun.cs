using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// PiSA-SR auf einem ganzen Bild, in Kacheln — wie <c>PiSASR_eval</c> im Modus „Standard“: Original bikubisch ×4, in den
/// Latentraum (VAE-Encoder), ein Schritt UNet (Rauschen vorhersagen und abziehen), zurück ins Bild (VAE-Decoder), Farben
/// angleichen (AdaIN). Alle drei Teile laufen in Kacheln mit weichen Übergängen; nichts davon braucht je das ganze
/// Bild in voller Größe als Gleitkomma. Gerechnet wird streifenweise von oben nach unten (je Decoder-Zeile so viel Encoder
/// und UNet wie nötig); fertige Flächen werden gemeldet, sobald keine spätere Kachel sie mehr berührt.
/// </summary>
public static class PiSaSrRun
{
    // Kacheln in Latentpixeln (1 Latentpixel = 8 Bildpixel). Das UNet ist auf 512 px (64 Latent) trainiert; größere
    // Kacheln brauchen in der Aufmerksamkeit quadratisch mehr Speicher.
    private const int EncoderTile = 96, EncoderOverlap = 16;
    private const int UnetTile = 64, UnetOverlap = 16;
    private const int DecoderTile = 64, DecoderOverlap = 12;

    // Geschätzte Kosten je Latentpixel (für den Fortschritt; gemessen auf CPU, nur das Verhältnis zählt)
    private const double EncoderCost = 1.45, UnetCost = 1.06, DecoderCost = 3.4;   // 500 × 300: 80 s, 65 s, 210 s

    /// <summary>
    /// Rechenaufwand für ein Original dieser Größe — Kacheln × Kachelfläche × Kosten je Teil, wie <see cref="Run"/>
    /// rechnet (mit Überlappung). Grundlage der Zeitschätzung: Zeit = Sekunden je Einheit × Aufwand.
    /// </summary>
    public static double Work(int w, int h)
    {
        int lw = (4 * w + 7) / 8, lh = (4 * h + 7) / 8;
        static double Part(int lw, int lh, int tile, int overlap, double cost)
        {
            var x = new TileAxis(lw, tile, overlap);
            var y = new TileAxis(lh, tile, overlap);
            return x.Count * y.Count * (double)x.Size * y.Size * cost;
        }
        return Part(lw, lh, EncoderTile, EncoderOverlap, EncoderCost) + Part(lw, lh, UnetTile, UnetOverlap, UnetCost)
               + Part(lw, lh, DecoderTile, DecoderOverlap, DecoderCost);
    }

    /// <param name="done">Fertige Fläche (in Pixeln des Originals) — aus dem Rechen-Thread aufgerufen.</param>
    public static UpscaleResult Run(MagickImage source, InferenceSession encoder, InferenceSession unet, InferenceSession decoder,
        IProgress<double>? progress, CancellationToken ct, Action<UpscaleResult, SourceRect>? done = null)
    {
        int w = (int)source.Width, h = (int)source.Height;
        var channels = source.HasAlpha ? 4 : 3;
        var src = AiEnhancer.ReadPixels(source, channels == 4 ? "RGBA" : "RGB");
        int ow = w * 4, oh = h * 4;
        int lw = (ow + 7) / 8, lh = (oh + 7) / 8;   // VAE braucht Vielfache von 8; der Überstand wird am Ende abgeschnitten
        float Src(int c, int x, int y) => src[((long)Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)) * channels + c] / 65535f;

        var ex = new TileAxis(lw, EncoderTile, EncoderOverlap);
        var ey = new TileAxis(lh, EncoderTile, EncoderOverlap);
        var ux = new TileAxis(lw, UnetTile, UnetOverlap);
        var uy = new TileAxis(lh, UnetTile, UnetOverlap);
        var dx = new TileAxis(lw, DecoderTile, DecoderOverlap);
        var dy = new TileAxis(lh, DecoderTile, DecoderOverlap);
        var dxp = dx.Scaled(8, DecoderOverlap);
        var dyp = dy.Scaled(8, DecoderOverlap);
        var total = ex.Count * ey.Count * ex.Size * ey.Size * EncoderCost + ux.Count * uy.Count * ux.Size * uy.Size * UnetCost
                    + dx.Count * dy.Count * dx.Size * dy.Size * DecoderCost;
        double spent = 0;
        void Advance(double cost)
        {
            spent += cost;
            progress?.Report(Math.Min(1, spent / total));
        }

        var result = new UpscaleResult(source, src, channels);
        var raw = result.Raw;
        var colors = new ColorMatch(src, channels, w * h);

        // Streifenweise von oben nach unten: jede Zeile Decoder-Kacheln, sobald die Zeilen darüber durch Encoder und UNet
        // fertig sind — so erscheinen fertige Bildteile über die ganze Rechnung verteilt, nicht erst am Ende. Eine
        // Latentzeile ist fertig, wenn alle Kacheln, die sie berühren, gerechnet sind; das sind die mit Anfang davor.
        var latent = new float[4 * lh * lw];
        var pred = new float[latent.Length];
        int encoderRow = 0, unetRow = 0;

        // 1) Encoder bis Latentzeile rowEnd: bikubisch ×4 → −1 … 1 → Latent (Mittelwert · 0,18215)
        void EncodeUpTo(int rowEnd)
        {
            for (; encoderRow < ey.Count && ey.Starts[encoderRow] < rowEnd; encoderRow++)
            for (var kx = 0; kx < ex.Count; kx++)
            {
                ct.ThrowIfCancellationRequested();
                int tx = ex.Starts[kx], ty = ey.Starts[encoderRow], tw = ex.Size, th = ey.Size;
                var image = Resample4.Up(Src, 3, tx * 8, ty * 8, tw * 8, th * 8, Resample4.Kernel.Bicubic);
                for (var i = 0; i < image.Length; i++) image[i] = Math.Clamp(image[i], 0f, 1f) * 2 - 1;
                var lat = RunOne(encoder, "image", image, [1, 3, th * 8, tw * 8], ct);
                Accumulate(latent, lat, lw, lh, tx, ty, tw, th, ex.Weights[kx], ey.Weights[encoderRow]);
                Advance(tw * th * EncoderCost);
            }
        }

        // 2) UNet bis Latentzeile rowEnd: Vorhersage je Kachel, weich zusammengesetzt
        void PredictUpTo(int rowEnd)
        {
            for (; unetRow < uy.Count && uy.Starts[unetRow] < rowEnd; unetRow++)
            {
                EncodeUpTo(uy.Starts[unetRow] + uy.Size);
                for (var kx = 0; kx < ux.Count; kx++)
                {
                    ct.ThrowIfCancellationRequested();
                    int tx = ux.Starts[kx], ty = uy.Starts[unetRow], tw = ux.Size, th = uy.Size;
                    var p = RunOne(unet, "latent", Cut(latent, lw, lh, tx, ty, tw, th), [1, 4, th, tw], ct);
                    Accumulate(pred, p, lw, lh, tx, ty, tw, th, ux.Weights[kx], uy.Weights[unetRow]);
                    Advance(tw * th * UnetCost);
                }
            }
        }

        // 3) Decoder: Latent − Vorhersage, Kachel für Kachel ins 16-Bit-Ergebnis (Gewichte ergeben zusammen 1 → direkt aufaddieren)
        for (var ky = 0; ky < dy.Count; ky++)
        for (var kx = 0; kx < dx.Count; kx++)
        {
            ct.ThrowIfCancellationRequested();
            int tx = dx.Starts[kx], ty = dy.Starts[ky], tw = dx.Size, th = dy.Size;
            EncodeUpTo(ty + th);
            PredictUpTo(ty + th);
            var z = Cut(latent, lw, lh, tx, ty, tw, th);
            var zp = Cut(pred, lw, lh, tx, ty, tw, th);
            for (var i = 0; i < z.Length; i++) z[i] -= zp[i];
            var img = RunOne(decoder, "latent", z, [1, 4, th, tw], ct);
            int pw = tw * 8, ph = th * 8, px = tx * 8, py = ty * 8;
            var wx = dxp.Weights[kx];
            var wy = dyp.Weights[ky];
            Parallel.For(0, Math.Min(ph, oh - py), ComputeLimits.Options, y =>
            {
                for (var x = 0; x < Math.Min(pw, ow - px); x++)
                {
                    var wgt = wx[x] * wy[y] * 65535f;
                    var di = ((long)(py + y) * ow + px + x) * 3;
                    for (var c = 0; c < 3; c++)
                    {
                        var v = (Math.Clamp(img[c * pw * ph + y * pw + x], -1f, 1f) + 1) * 0.5f;
                        raw[di + c] = (ushort)Math.Min(65535, raw[di + c] + (int)(v * wgt + 0.5f));
                    }
                }
            });

            // Fertig sind jetzt die Pixel, die keine spätere Kachel mehr berührt: ein Rechteck
            var (fx0, fx1) = Finished(dxp, kx, ow);
            var (fy0, fy1) = Finished(dyp, ky, oh);
            var finished = new SourceRect(fx0 / 4, fy0 / 4, (fx1 - fx0) / 4, (fy1 - fy0) / 4);
            if (!finished.IsEmpty)
            {
                colors.Add(raw, ow, finished);
                colors.Apply(result);
                done?.Invoke(result, finished);
            }
            Advance(tw * th * DecoderCost);
        }
        return result;
    }

    /// <summary>Bereich [von, bis) in Bildpixeln, dessen letzte berührende Kachel <paramref name="k"/> ist.</summary>
    private static (int From, int To) Finished(TileAxis axis, int k, int limit)
    {
        int from = -1, to = -1;
        for (var p = axis.Starts[k]; p < Math.Min(limit, axis.Starts[k] + axis.Size); p++)
        {
            if (axis.LastCovering[p] != k) continue;
            if (from < 0) from = p;
            to = p + 1;
        }
        return from < 0 ? (0, 0) : (from, to);
    }

    private static float[] Cut(float[] latent, int lw, int lh, int tx, int ty, int tw, int th)
    {
        var tile = new float[4 * tw * th];
        for (var c = 0; c < 4; c++)
        for (var y = 0; y < th; y++)
            Array.Copy(latent, c * lw * lh + (ty + y) * lw + tx, tile, c * tw * th + y * tw, tw);
        return tile;
    }

    private static float[] RunOne(InferenceSession session, string input, float[] data, long[] shape, CancellationToken ct)
    {
        using var value = OrtValue.CreateTensorValueFromMemory(data, shape);
        return OnnxSessions.Run(session, new Dictionary<string, OrtValue> { [input] = value }, ct);
    }

    /// <summary>Kachel [c][th][tw] gewichtet in [c][lh][lw] aufaddieren.</summary>
    private static void Accumulate(float[] target, float[] tile, int lw, int lh, int tx, int ty, int tw, int th, float[] wx, float[] wy)
    {
        var channels = tile.Length / (tw * th);
        for (var c = 0; c < channels; c++)
        for (var y = 0; y < th; y++)
        {
            var row = c * lw * lh + (ty + y) * lw + tx;
            var src = c * tw * th + y * tw;
            for (var x = 0; x < tw; x++) target[row + x] += tile[src + x] * wx[x] * wy[y];
        }
    }

    /// <summary>
    /// AdaIN wie PiSA-SR (<c>adain_color_fix</c>): Mittelwert und Streuung je Farbkanal auf die des Originals bringen —
    /// das rohe Ergebnis ist sonst etwas dunkler und wärmer. Die Werte des Ergebnisses wachsen mit jeder fertigen Fläche;
    /// am Ende sind es die des ganzen Bildes.
    /// </summary>
    private sealed class ColorMatch
    {
        private readonly double[] _srcMean = new double[3], _srcStd = new double[3];
        private readonly double[] _sum = new double[3], _sq = new double[3];
        private long _count;

        public ColorMatch(ushort[] src, int channels, long pixels)
        {
            for (var c = 0; c < 3; c++)
            {
                double s = 0, q = 0;
                for (long i = c; i < pixels * channels; i += channels)
                {
                    var v = src[i] / 65535.0;
                    s += v;
                    q += v * v;
                }
                _srcMean[c] = s / pixels;
                _srcStd[c] = Math.Sqrt(Math.Max(0, q / pixels - _srcMean[c] * _srcMean[c]));
            }
        }

        public void Add(ushort[] raw, int ow, SourceRect r)
        {
            for (var y = r.Y * 4; y < (r.Y + r.Height) * 4; y++)
            for (long i = ((long)y * ow + r.X * 4) * 3, end = i + r.Width * 4 * 3; i < end; i += 3)
            for (var c = 0; c < 3; c++)
            {
                var v = raw[i + c] / 65535.0;
                _sum[c] += v;
                _sq[c] += v * v;
            }
            _count += (long)r.Width * r.Height * 16;
        }

        public void Apply(UpscaleResult result)
        {
            for (var c = 0; c < 3; c++)
            {
                var mean = _sum[c] / _count;
                var std = Math.Sqrt(Math.Max(0, _sq[c] / _count - mean * mean));
                var gain = std > 1e-6 ? _srcStd[c] / std : 1;
                result.SetColorFix(c, (float)gain, (float)(_srcMean[c] - mean * gain));
            }
        }
    }
}
