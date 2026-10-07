namespace ImageConverter.Core.Imaging;

/// <summary>
/// Unscharfmaskierung (Unsharp Mask) auf 8-Bit-BGRA-Pixeln — schnell genug für eine
/// Live-Vorschau beim Ziehen am Regler. Vorschau und Export nutzen dieselbe Funktion,
/// damit man exportiert, was man gesehen hat.
///
/// Der Weichzeichner ist ein Gauß, angenähert durch drei Boxfilter-Durchgänge
/// (Kutskir/Wells). Die Laufzeit hängt dadurch nicht vom Radius ab.
/// </summary>
public static class Sharpen
{
    /// <param name="amount">Stärke, 0 = aus; 1 = Kanten um 100 % verstärken.</param>
    /// <param name="sigma">Radius des Weichzeichners in Pixeln (Gauß-Sigma).</param>
    /// <param name="threshold">Mindestunterschied in Tonwertstufen (0–255), damit flache
    /// Flächen und Rauschen nicht mitgeschärft werden.</param>
    public static byte[] UnsharpMask(byte[] bgra, int width, int height,
        double amount, double sigma, double threshold = 2)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bgra.Length, width * height * 4, nameof(bgra));

        var result = (byte[])bgra.Clone();
        if (amount <= 0.001 || sigma <= 0.05) return result;

        var pixels = width * height;
        var blur = new float[pixels * 3];
        var tmp = new float[pixels * 3];

        // B, G, R in eine Fließkomma-Ebene; Alpha wird nicht geschärft
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var p = row + x;
                blur[p * 3] = bgra[p * 4];
                blur[p * 3 + 1] = bgra[p * 4 + 1];
                blur[p * 3 + 2] = bgra[p * 4 + 2];
            }
        });

        if (sigma < ExactGaussBelow)
        {
            // Kleine Radien (typisch für Fotos): echter Gauß-Kernel, die Box-Näherung
            // kann so feine Weichzeichnung nicht darstellen. Bei ≤ 6 Abgriffen billig.
            var kernel = GaussKernel(sigma);
            ConvolveHorizontal(blur, tmp, width, height, kernel);
            ConvolveVertical(tmp, blur, width, height, kernel);
        }
        else
        {
            foreach (var radius in BoxRadii(sigma, passes: 3))
            {
                BoxHorizontal(blur, tmp, width, height, radius);
                BoxVertical(tmp, blur, width, height, radius);
            }
        }

        var amt = (float)amount;
        var thr = (float)threshold;
        Parallel.For(0, height, y =>
        {
            var row = y * width;
            for (var x = 0; x < width; x++)
            {
                var p = row + x;
                for (var c = 0; c < 3; c++)
                {
                    float original = bgra[p * 4 + c];
                    var diff = original - blur[p * 3 + c];
                    if (MathF.Abs(diff) < thr) continue;
                    var v = original + amt * diff;
                    result[p * 4 + c] = (byte)(v <= 0 ? 0 : v >= 255 ? 255 : v + 0.5f);
                }
            }
        });

        return result;
    }

    /// <summary>Ab hier reicht die Box-Näherung; darunter echter Gauß.</summary>
    internal const double ExactGaussBelow = 2.0;

    internal static float[] GaussKernel(double sigma)
    {
        var r = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var k = new float[2 * r + 1];
        double sum = 0;
        for (var i = -r; i <= r; i++)
        {
            var v = Math.Exp(-(i * i) / (2 * sigma * sigma));
            k[i + r] = (float)v;
            sum += v;
        }
        for (var i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);
        return k;
    }

    private static void ConvolveHorizontal(float[] src, float[] dst, int w, int h, float[] k)
    {
        var r = k.Length / 2;
        Parallel.For(0, h, y =>
        {
            var row = y * w * 3;
            for (var x = 0; x < w; x++)
            for (var c = 0; c < 3; c++)
            {
                float acc = 0;
                for (var i = -r; i <= r; i++)
                    acc += k[i + r] * src[row + Math.Clamp(x + i, 0, w - 1) * 3 + c];
                dst[row + x * 3 + c] = acc;
            }
        });
    }

    private static void ConvolveVertical(float[] src, float[] dst, int w, int h, float[] k)
    {
        var r = k.Length / 2;
        var stride = w * 3;
        Parallel.For(0, h, y =>
        {
            var outp = y * stride;
            for (var i = -r; i <= r; i++)
            {
                var weight = k[i + r];
                var inp = Math.Clamp(y + i, 0, h - 1) * stride;
                if (i == -r)
                    for (var j = 0; j < stride; j++) dst[outp + j] = weight * src[inp + j];
                else
                    for (var j = 0; j < stride; j++) dst[outp + j] += weight * src[inp + j];
            }
        });
    }

    /// <summary>Boxradien, deren Hintereinanderausführung einen Gauß mit <paramref name="sigma"/> ergibt.</summary>
    internal static int[] BoxRadii(double sigma, int passes)
    {
        var wIdeal = Math.Sqrt(12 * sigma * sigma / passes + 1);
        var wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        var wu = wl + 2;
        var mIdeal = (12 * sigma * sigma - passes * wl * wl - 4 * passes * wl - 3 * passes) / (-4.0 * wl - 4);
        var m = (int)Math.Round(mIdeal);

        var radii = new int[passes];
        for (var i = 0; i < passes; i++)
            radii[i] = ((i < m ? wl : wu) - 1) / 2;
        return radii;
    }

    // Laufende Summe je Zeile; Ränder werden mit dem Randpixel fortgesetzt.
    private static void BoxHorizontal(float[] src, float[] dst, int w, int h, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, src.Length); return; }
        var inv = 1f / (2 * r + 1);

        Parallel.For(0, h, y =>
        {
            var row = y * w * 3;
            for (var c = 0; c < 3; c++)
            {
                var first = src[row + c];
                var val = r * first;
                for (var j = 0; j < r; j++)
                    val += src[row + Math.Min(j, w - 1) * 3 + c];

                for (var x = 0; x < w; x++)
                {
                    val += src[row + Math.Min(x + r, w - 1) * 3 + c];
                    dst[row + x * 3 + c] = val * inv;
                    val -= src[row + Math.Max(x - r, 0) * 3 + c];
                }
            }
        });
    }

    // Spaltenweise in Blöcken von 64 Spalten, Zeile für Zeile — speicherfreundlich.
    private static void BoxVertical(float[] src, float[] dst, int w, int h, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, src.Length); return; }
        var inv = 1f / (2 * r + 1);
        const int block = 64;
        var blocks = (w + block - 1) / block;
        var stride = w * 3;

        Parallel.For(0, blocks, b =>
        {
            var x0 = b * block * 3;
            var len = Math.Min(block, w - b * block) * 3;
            var val = new float[len];

            for (var k = 0; k < len; k++)
                val[k] = r * src[x0 + k];
            for (var j = 0; j < r; j++)
            {
                var off = Math.Min(j, h - 1) * stride + x0;
                for (var k = 0; k < len; k++) val[k] += src[off + k];
            }

            for (var y = 0; y < h; y++)
            {
                var add = Math.Min(y + r, h - 1) * stride + x0;
                var outp = y * stride + x0;
                var sub = Math.Max(y - r, 0) * stride + x0;
                for (var k = 0; k < len; k++)
                {
                    val[k] += src[add + k];
                    dst[outp + k] = val[k] * inv;
                    val[k] -= src[sub + k];
                }
            }
        });
    }
}
