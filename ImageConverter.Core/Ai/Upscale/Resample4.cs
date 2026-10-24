using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Ai.Upscale;

/// <summary>
/// Enlarges and reduces by exactly a factor of 4 on crops (float planes, row by row per channel), for the fidelity
/// constraint and as input of PiSA-SR.
/// </summary>
/// <remarks>
/// Pixel centres as in ImageMagick and common implementations: source pixel i covers target pixels 4i … 4i+3. Outside the crop the edge pixel
/// is repeated. The channels are processed in parallel.
/// </remarks>
public static class Resample4
{
    /// <summary>Interpolation kernel for enlarging.</summary>
    public enum Kernel
    {
        /// <summary>Bicubic (a = −0.5, as in common implementations): input of PiSA-SR, back projection.</summary>
        Bicubic,

        /// <summary>Lanczos 3: plain enlargement without AI (for the details blend and the alpha channel).</summary>
        Lanczos,
    }

    private static double Bicubic(double x)
    {
        const double a = -0.5;
        x = Math.Abs(x);
        if (x < 1) return ((a + 2) * x - (a + 3)) * x * x + 1;
        if (x < 2) return ((a * x - 5 * a) * x + 8 * a) * x - 4 * a;
        return 0;
    }

    private static double Lanczos3(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-9) return 1;
        if (x >= 3) return 0;
        var px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }

    /// <summary>Per phase (target pixel mod 4): offsets to the source pixel and weights (sum 1).</summary>
    private static readonly Dictionary<Kernel, (int[] Offsets, float[] Weights)[]> UpTaps = new()
    {
        [Kernel.Bicubic] = BuildUp(Bicubic, 2),
        [Kernel.Lanczos] = BuildUp(Lanczos3, 3),
    };

    private static (int[] Offsets, float[] Weights)[] BuildUp(Func<double, double> k, int support)
    {
        var phases = new (int[], float[])[4];
        for (var p = 0; p < 4; p++)
        {
            var delta = (p + 0.5) / 4 - 0.5;          // position in the source grid relative to pixel i
            var f = (int)Math.Floor(delta);
            var offs = Enumerable.Range(f - support + 1, 2 * support).ToArray();
            var w = offs.Select(o => k(delta - o)).ToArray();
            var sum = w.Sum();
            phases[p] = (offs, w.Select(v => (float)(v / sum)).ToArray());
        }
        return phases;
    }

    /// <summary>Reduction ÷4 with Lanczos 3 (stretched to 12 large-grid pixels per side): offsets X − 4i and weights.</summary>
    private static readonly (int[] Offsets, float[] Weights) DownTaps = BuildDown();

    private static (int[] Offsets, float[] Weights) BuildDown()
    {
        var offs = Enumerable.Range(-10, 24).ToArray();   // large-grid pixels 4i−10 … 4i+13
        var w = offs.Select(o => Lanczos3((o + 0.5 - 2) / 4)).ToArray();
        var sum = w.Sum();
        return (offs, w.Select(v => (float)(v / sum)).ToArray());
    }

    /// <summary>
    /// Enlarges ×4 the source area that covers the target pixels <paramref name="x0"/> … x0 + ow − 1 (and likewise in y).
    /// </summary>
    /// <param name="source">Returns a value for (channel, x, y) in source pixels and clamps to the image itself.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="x0">First target column (in pixels of the ×4 grid).</param>
    /// <param name="y0">First target row (in pixels of the ×4 grid).</param>
    /// <param name="ow">Number of target columns.</param>
    /// <param name="oh">Number of target rows.</param>
    /// <param name="kernel">Interpolation kernel.</param>
    /// <returns>Planes [channels][oh][ow].</returns>
    public static float[] Up(Func<int, int, int, float> source, int channels, int x0, int y0, int ow, int oh, Kernel kernel)
    {
        var taps = UpTaps[kernel];
        int sx0 = (int)Math.Floor(x0 / 4.0) - 4, sx1 = (int)Math.Floor((x0 + ow - 1) / 4.0) + 4;
        int sy0 = (int)Math.Floor(y0 / 4.0) - 4, sy1 = (int)Math.Floor((y0 + oh - 1) / 4.0) + 4;
        int sw = sx1 - sx0 + 1, sh = sy1 - sy0 + 1;
        var result = new float[channels * ow * oh];
        Parallel.For(0, channels, ComputeLimits.Options, c =>
        {
            // Read the source, then filter horizontally, then vertically
            var src = new float[sw * sh];
            for (var y = 0; y < sh; y++)
            for (var x = 0; x < sw; x++) src[y * sw + x] = source(c, sx0 + x, sy0 + y);
            var horiz = new float[sh * ow];
            for (var x = 0; x < ow; x++)
            {
                var gx = x0 + x;
                var i = (int)Math.Floor(gx / 4.0);
                var (offs, w) = taps[gx - 4 * i];
                for (var y = 0; y < sh; y++)
                {
                    float s = 0;
                    for (var t = 0; t < offs.Length; t++) s += w[t] * src[y * sw + (i + offs[t] - sx0)];
                    horiz[y * ow + x] = s;
                }
            }
            var plane = c * ow * oh;
            for (var y = 0; y < oh; y++)
            {
                var gy = y0 + y;
                var i = (int)Math.Floor(gy / 4.0);
                var (offs, w) = taps[gy - 4 * i];
                for (var x = 0; x < ow; x++)
                {
                    float s = 0;
                    for (var t = 0; t < offs.Length; t++) s += w[t] * horiz[(i + offs[t] - sy0) * ow + x];
                    result[plane + y * ow + x] = s;
                }
            }
        });
        return result;
    }

    /// <summary>Enlarges planes [channels][h][w] to [channels][4h][4w] (edge pixels repeated).</summary>
    /// <param name="planes">Source planes.</param>
    /// <param name="w">Source width in pixels.</param>
    /// <param name="h">Source height in pixels.</param>
    /// <param name="channels">Number of channels.</param>
    /// <param name="kernel">Interpolation kernel.</param>
    /// <returns>Planes [channels][4h][4w].</returns>
    public static float[] Up(float[] planes, int w, int h, int channels, Kernel kernel) =>
        Up((c, x, y) => planes[c * w * h + Math.Clamp(y, 0, h - 1) * w + Math.Clamp(x, 0, w - 1)], channels, 0, 0, 4 * w, 4 * h, kernel);

    /// <summary>Reduces planes [channels][4h][4w] to [channels][h][w] with Lanczos 3 (edge pixels repeated).</summary>
    /// <param name="planes">Planes at four times the size.</param>
    /// <param name="w">Target width in pixels.</param>
    /// <param name="h">Target height in pixels.</param>
    /// <param name="channels">Number of channels.</param>
    /// <returns>Planes [channels][h][w].</returns>
    public static float[] Down(float[] planes, int w, int h, int channels)
    {
        int bw = 4 * w, bh = 4 * h;
        var (offs, wt) = DownTaps;
        var result = new float[channels * w * h];
        Parallel.For(0, channels, ComputeLimits.Options, c =>
        {
            var plane = c * bw * bh;
            var horiz = new float[bh * w];
            for (var y = 0; y < bh; y++)
            for (var i = 0; i < w; i++)
            {
                float s = 0;
                for (var t = 0; t < offs.Length; t++) s += wt[t] * planes[plane + y * bw + Math.Clamp(4 * i + offs[t], 0, bw - 1)];
                horiz[y * w + i] = s;
            }
            for (var j = 0; j < h; j++)
            for (var i = 0; i < w; i++)
            {
                float s = 0;
                for (var t = 0; t < offs.Length; t++) s += wt[t] * horiz[Math.Clamp(4 * j + offs[t], 0, bh - 1) * w + i];
                result[c * w * h + j * w + i] = s;
            }
        });
        return result;
    }

    /// <summary>
    /// Fidelity constraint (back projection): E ← E + U(S − V(E)), repeated.
    /// </summary>
    /// <remarks>
    /// Afterwards the result, reduced back to the original size, gives (almost) exactly the original: the AI may add
    /// fine detail but must not shift brightness, colour or shapes. The effect is local: only a margin of a little over
    /// 5 source pixels per round depends on the area outside.
    /// </remarks>
    /// <param name="e">Enlarged result [3][4h][4w]; it is overwritten.</param>
    /// <param name="s">Original [3][h][w].</param>
    /// <param name="w">Width of the original in pixels.</param>
    /// <param name="h">Height of the original in pixels.</param>
    /// <param name="rounds">Number of rounds.</param>
    public static void BackProject(float[] e, float[] s, int w, int h, int rounds = 3)
    {
        for (var r = 0; r < rounds; r++)
        {
            var v = Down(e, w, h, 3);
            for (var i = 0; i < v.Length; i++) v[i] = s[i] - v[i];
            var up = Up(v, w, h, 3, Kernel.Bicubic);
            for (var i = 0; i < e.Length; i++) e[i] += up[i];
        }
    }
}
