using ImageMagick;
using Microsoft.ML.OnnxRuntime;
using ImageConverter.Core.Platform;

namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// Runs PiSA-SR on a whole image in tiles, as <c>PiSASR_eval</c> does in default mode.
/// </summary>
/// <remarks>
/// <para>
/// Steps: original bicubic ×4, into the latent space (VAE encoder), one UNet step (predict the noise and subtract it),
/// back into the image (VAE decoder), colour matching (AdaIN). All three parts run in tiles with soft transitions;
/// none of them ever needs the whole image at full size as floating point.
/// </para>
/// <para>
/// The computation proceeds in stripes from top to bottom (per decoder row only as much encoder and UNet work as
/// needed); finished areas are reported as soon as no later tile touches them. See docs/FEHLERGEDAECHTNIS.md, No. 65.
/// </para>
/// </remarks>
public static class PiSaSrRun
{
    // Tiles in latent pixels (1 latent pixel = 8 image pixels). The UNet was trained on 512 px (64 latent); larger tiles
    // need quadratically more memory in the attention. See docs/FEHLERGEDAECHTNIS.md, No. 61.
    private const int EncoderTile = 96, EncoderOverlap = 16;
    private const int UnetTile = 64, UnetOverlap = 16;
    private const int DecoderTile = 64, DecoderOverlap = 12;

    // Estimated cost per latent pixel (for the progress; measured on the CPU, only the ratio matters)
    private const double EncoderCost = 1.45, UnetCost = 1.06, DecoderCost = 3.4;   // 500 × 300: 80 s, 65 s, 210 s

    /// <summary>
    /// Returns the computing effort for an original of this size: tiles × tile area × cost per part, tiled as
    /// <see cref="Run"/> does (with overlap).
    /// </summary>
    /// <param name="w">Width of the original in pixels.</param>
    /// <param name="h">Height of the original in pixels.</param>
    /// <returns>Effort in arbitrary units; basis of the time estimate: time = seconds per unit × effort.</returns>
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

    /// <summary>Upscales an image ×4 with PiSA-SR.</summary>
    /// <param name="source">Original image; it is not modified.</param>
    /// <param name="encoder">Session of the VAE encoder.</param>
    /// <param name="unet">Session of the UNet.</param>
    /// <param name="decoder">Session of the VAE decoder.</param>
    /// <param name="progress">Receives the completed fraction, 0…1, weighted by the estimated cost of each part.</param>
    /// <param name="ct">Cancels the computation; checked before each tile and passed to the models.</param>
    /// <param name="done">
    /// Called with each finished area (in pixels of the original), on the computing thread. The colour matching is
    /// updated before each call, so areas reported earlier may change slightly in colour.
    /// </param>
    /// <returns>The raw result; it is filled while the run proceeds.</returns>
    public static UpscaleResult Run(MagickImage source, InferenceSession encoder, InferenceSession unet, InferenceSession decoder,
        IProgress<double>? progress, CancellationToken ct, Action<UpscaleResult, SourceRect>? done = null)
    {
        int w = (int)source.Width, h = (int)source.Height;
        var channels = source.HasAlpha ? 4 : 3;
        var src = AiEnhancer.ReadPixels(source, channels == 4 ? "RGBA" : "RGB");
        int ow = w * 4, oh = h * 4;
        int lw = (ow + 7) / 8, lh = (oh + 7) / 8;   // the VAE needs multiples of 8; the excess is cut off at the end
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

        // In stripes from top to bottom: each row of decoder tiles as soon as the rows above have passed encoder and UNet,
        // so finished parts of the image appear throughout the computation, not only at the end. A latent row is finished
        // when all tiles touching it are computed; those are the tiles starting before it.
        var latent = new float[4 * lh * lw];
        var pred = new float[latent.Length];
        int encoderRow = 0, unetRow = 0;

        // 1) Encoder up to latent row rowEnd: bicubic ×4 → −1 … 1 → latent (mean · 0.18215)
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

        // 2) UNet up to latent row rowEnd: prediction per tile, blended softly
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

        // 3) Decoder: latent − prediction, tile by tile into the 16-bit result (the weights sum to 1, so they are added directly)
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

            // Finished now are the pixels that no later tile touches: a rectangle
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

    /// <summary>Range [from, to) in image pixels whose last touching tile is <paramref name="k"/>.</summary>
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

    /// <summary>Adds tile [c][th][tw], weighted, into [c][lh][lw].</summary>
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
    /// AdaIN as in PiSA-SR (<c>adain_color_fix</c>): brings mean and standard deviation per colour channel to those of
    /// the original; otherwise the raw result is slightly darker and warmer. The statistics of the result grow with
    /// each finished area; at the end they cover the whole image.
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
