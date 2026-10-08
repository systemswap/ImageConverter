namespace ImageConverter.Core.Imaging;

/// <summary>
/// Unscharfmaskierung (Unsharp Mask) auf 8-Bit-BGRA-Pixeln — schnell genug für eine
/// Live-Vorschau beim Ziehen am Regler. Vorschau und Export nutzen dieselbe Funktion,
/// damit man exportiert, was man gesehen hat.
///
/// Der Weichzeichner ist <see cref="Blur.Gauss"/> (Gauß, bei größeren Radien über drei
/// Boxfilter-Durchgänge angenähert).
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

        Blur.Gauss(blur, width, height, sigma, channels: 3);

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

    /// <summary>Wie oben auf Fließkomma-Pixeln (sRGB-kodiert 0–1), ohne Rundung — für die Dunst-Durchläufe.</summary>
    internal static float[] UnsharpMask(float[] px, int width, int height,
        double amount, double sigma, double threshold = 2)
    {
        var result = (float[])px.Clone();
        if (amount <= 0.001 || sigma <= 0.05) return result;

        var pixels = width * height;
        var blur = new float[pixels * 3];
        Parallel.For(0, pixels, p =>
        {
            blur[p * 3] = px[p * 4];
            blur[p * 3 + 1] = px[p * 4 + 1];
            blur[p * 3 + 2] = px[p * 4 + 2];
        });

        Blur.Gauss(blur, width, height, sigma, channels: 3);

        var amt = (float)amount;
        var thr = (float)threshold / 255;
        Parallel.For(0, pixels, p =>
        {
            for (var c = 0; c < 3; c++)
            {
                var original = px[p * 4 + c];
                var diff = original - blur[p * 3 + c];
                if (MathF.Abs(diff) < thr) continue;
                result[p * 4 + c] = Math.Clamp(original + amt * diff, 0, 1);
            }
        });

        return result;
    }
}
