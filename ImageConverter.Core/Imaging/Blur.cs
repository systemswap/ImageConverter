namespace ImageConverter.Core.Imaging;

/// <summary>
/// Gauß-Weichzeichner auf Fließkomma-Ebenen mit beliebig vielen Kanälen (verschachtelt,
/// z. B. B,G,R,B,G,R …). Gebraucht vom Schärfen (3 Kanäle) und von Tiefen/Lichter
/// (1 Kanal Helligkeit).
///
/// Kleine Radien mit echtem Gauß-Kernel, größere mit drei Boxfilter-Durchgängen
/// (Kutskir/Wells) — die Laufzeit hängt dann nicht vom Radius ab.
/// </summary>
internal static class Blur
{
    /// <summary>Ab hier reicht die Box-Näherung; darunter echter Gauß.</summary>
    internal const double ExactGaussBelow = 2.0;

    /// <summary>Zeichnet <paramref name="data"/> an Ort und Stelle weich.</summary>
    public static void Gauss(float[] data, int width, int height, double sigma, int channels = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(data.Length, width * height * channels, nameof(data));
        var tmp = PixelPool.Rent(data.Length);   // wird vor dem Lesen vollständig geschrieben

        if (sigma < ExactGaussBelow)
        {
            // Kleine Radien (typisch fürs Schärfen von Fotos): die Box-Näherung kann so
            // feine Weichzeichnung nicht darstellen. Bei ≤ 6 Abgriffen billig.
            var kernel = GaussKernel(sigma);
            ConvolveHorizontal(data, tmp, width, height, channels, kernel);
            ConvolveVertical(tmp, data, width, height, channels, kernel);
        }
        else
        {
            foreach (var radius in BoxRadii(sigma, passes: 3))
            {
                BoxHorizontal(data, tmp, width, height, channels, radius);
                BoxVertical(tmp, data, width, height, channels, radius);
            }
        }
        PixelPool.Return(tmp);
    }

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

    private static void ConvolveHorizontal(float[] src, float[] dst, int w, int h, int ch, float[] k)
    {
        var r = k.Length / 2;
        Parallel.For(0, h, y =>
        {
            var row = y * w * ch;
            for (var x = 0; x < w; x++)
            for (var c = 0; c < ch; c++)
            {
                float acc = 0;
                for (var i = -r; i <= r; i++)
                    acc += k[i + r] * src[row + Math.Clamp(x + i, 0, w - 1) * ch + c];
                dst[row + x * ch + c] = acc;
            }
        });
    }

    private static void ConvolveVertical(float[] src, float[] dst, int w, int h, int ch, float[] k)
    {
        var r = k.Length / 2;
        var stride = w * ch;
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
    private static void BoxHorizontal(float[] src, float[] dst, int w, int h, int ch, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, src.Length); return; }
        var inv = 1f / (2 * r + 1);

        Parallel.For(0, h, y =>
        {
            var row = y * w * ch;
            for (var c = 0; c < ch; c++)
            {
                var first = src[row + c];
                var val = r * first;
                for (var j = 0; j < r; j++)
                    val += src[row + Math.Min(j, w - 1) * ch + c];

                for (var x = 0; x < w; x++)
                {
                    val += src[row + Math.Min(x + r, w - 1) * ch + c];
                    dst[row + x * ch + c] = val * inv;
                    val -= src[row + Math.Max(x - r, 0) * ch + c];
                }
            }
        });
    }

    // Spaltenweise in Blöcken von 64 Spalten, Zeile für Zeile — speicherfreundlich.
    private static void BoxVertical(float[] src, float[] dst, int w, int h, int ch, int r)
    {
        if (r <= 0) { Array.Copy(src, dst, src.Length); return; }
        var inv = 1f / (2 * r + 1);
        const int block = 64;
        var blocks = (w + block - 1) / block;
        var stride = w * ch;

        Parallel.For(0, blocks, b =>
        {
            var x0 = b * block * ch;
            var len = Math.Min(block, w - b * block) * ch;
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
