namespace ImageConverter.Core.Imaging;

/// <summary>
/// Dunst entfernen nach dem Dark Channel Prior (He, Sun, Tang 2009/2011) — dasselbe
/// physikalische Modell, auf das Adobe sein „Dunst entfernen“ stützt:
/// Bild = Szene · t + Himmelslicht · (1 − t), t = Durchlässigkeit der Luft.
///
/// Beobachtung: In klaren Bildern hat fast jede kleine Fläche irgendwo einen sehr dunklen
/// Farbkanal (Schatten, kräftige Farben). Dunst hellt genau diesen „dunklen Kanal“ auf —
/// daraus lässt sich t je Stelle schätzen und der Dunst herausrechnen. Die Schätzung wird
/// mit einem Guided Filter an die Kanten des Bildes angepasst, sonst entstehen Ränder um
/// Gegenstände vor dem Himmel.
/// </summary>
internal static class Dehaze
{
    /// <summary>Größe der Flächen für den dunklen Kanal (Radius), relativ zur längsten Kante.</summary>
    private const double PatchPerEdge = 0.012;

    /// <summary>Glättung der Durchlässigkeit (Guided Filter), relativ zur längsten Kante.</summary>
    private const double RefinePerEdge = 0.03;

    /// <summary>Kleinste Durchlässigkeit: begrenzt die Verstärkung (Rauschen, Himmel).</summary>
    private const float MinTransmission = 0.25f;

    /// <param name="amount">−1 … +1: rechts Dunst entfernen, links Dunst hinzufügen.</param>
    public static byte[] Apply(byte[] bgra, int width, int height, float amount)
    {
        var n = width * height;
        var result = (byte[])bgra.Clone();
        if (amount == 0) return result;

        var air = AirLight(bgra, width, height);

        if (amount < 0)
        {
            // Dunst hinzufügen: gleichmäßig zum Himmelslicht hin
            var k = -amount * 0.5f;
            Parallel.For(0, height, y =>
            {
                for (int x = 0, i = y * width * 4; x < width; x++, i += 4)
                    for (var c = 0; c < 3; c++)
                        result[i + c] = ToByte(bgra[i + c] / 255f + (air[c] - bgra[i + c] / 255f) * k);
            });
            return result;
        }

        // Dunkler Kanal des aufs Himmelslicht normierten Bildes → Durchlässigkeit
        var edge = Math.Max(width, height);
        var minRgb = new float[n];
        var gray = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;
                minRgb[p] = MathF.Min(r / air[2], MathF.Min(g / air[1], b / air[0]));
                gray[p] = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            }
        });
        var dark = MinFilter(minRgb, width, height, Math.Max(1, (int)Math.Round(PatchPerEdge * edge)));

        var omega = 0.95f * amount;
        var t = new float[n];
        for (var p = 0; p < n; p++) t[p] = 1 - omega * dark[p];
        t = Guided.Filter(gray, t, width, height, Math.Max(1.0, RefinePerEdge * edge), eps: 1e-3f);

        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var tt = Math.Clamp(t[p], MinTransmission, 1);
                var i = p * 4;
                for (var c = 0; c < 3; c++)
                    result[i + c] = ToByte((bgra[i + c] / 255f - air[c]) / tt + air[c]);
            }
        });
        return result;
    }

    /// <summary>
    /// Himmelslicht (B, G, R): Mittel der Pixel, deren dunkler Kanal zu den hellsten 0,1 %
    /// gehört — dort ist der Dunst am dichtesten. Mindestens 0,5, damit dunkle Bilder ohne
    /// Dunst nicht kaputtgerechnet werden.
    /// </summary>
    internal static float[] AirLight(byte[] bgra, int width, int height)
    {
        var n = width * height;
        var minRgb = new float[n];
        for (var p = 0; p < n; p++)
            minRgb[p] = Math.Min(bgra[p * 4], Math.Min(bgra[p * 4 + 1], bgra[p * 4 + 2])) / 255f;
        var dark = MinFilter(minRgb, width, height, Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(width, height))));

        // Schwelle der hellsten 0,1 % über ein Histogramm statt Sortieren (24 MP!)
        var count = Math.Max(1, n / 1000);
        var histogram = new int[1024];
        foreach (var v in dark) histogram[Math.Clamp((int)(v * 1023), 0, 1023)]++;
        var bin = 1023;
        for (var seen = 0; bin > 0 && (seen += histogram[bin]) < count; bin--) { }
        var threshold = bin / 1023f;
        double sb = 0, sg = 0, sr = 0;
        var used = 0;
        for (var p = 0; p < n && used < count; p++)
        {
            if (dark[p] < threshold) continue;
            sb += bgra[p * 4]; sg += bgra[p * 4 + 1]; sr += bgra[p * 4 + 2];
            used++;
        }
        return
        [
            (float)Math.Max(0.5, sb / used / 255),
            (float)Math.Max(0.5, sg / used / 255),
            (float)Math.Max(0.5, sr / used / 255),
        ];
    }

    /// <summary>Minimum über ein Quadrat mit Radius r — zerlegt in Zeilen und Spalten, O(n).</summary>
    internal static float[] MinFilter(float[] src, int width, int height, int r)
    {
        var rows = new float[src.Length];
        Parallel.For(0, height, y => SlidingMin(src, rows, y * width, 1, width, r));
        var dst = new float[src.Length];
        Parallel.For(0, width, x => SlidingMin(rows, dst, x, width, height, r));
        return dst;
    }

    /// <summary>Gleitendes Minimum über eine Zeile oder Spalte (monotone Warteschlange).</summary>
    private static void SlidingMin(float[] src, float[] dst, int start, int stride, int length, int r)
    {
        var queue = new int[length];
        int head = 0, tail = 0;
        for (int i = 0, added = 0; i < length; i++)
        {
            // Fenster [i − r, i + r]: rechts nachschieben
            for (; added <= Math.Min(length - 1, i + r); added++)
            {
                var v = src[start + added * stride];
                while (tail > head && src[start + queue[tail - 1] * stride] >= v) tail--;
                queue[tail++] = added;
            }
            while (queue[head] < i - r) head++;
            dst[start + i * stride] = src[start + queue[head] * stride];
        }
    }

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0, 1) * 255 + 0.5f);
}

/// <summary>
/// Guided Filter (He, Sun, Tang 2010): glättet <c>input</c>, folgt dabei aber den Kanten
/// von <c>guide</c> — pro Umgebung gilt das lineare Modell q = a · guide + b.
/// </summary>
internal static class Guided
{
    /// <param name="eps">Ab welcher Schwankung des Führungsbildes (als Varianz, Werte 0–1)
    /// eine Kante zählt.</param>
    public static float[] Filter(float[] guide, float[] input, int width, int height, double sigma, float eps)
    {
        var n = guide.Length;
        var meanI = (float[])guide.Clone();
        var meanP = (float[])input.Clone();
        var corrII = new float[n];
        var corrIP = new float[n];
        for (var i = 0; i < n; i++)
        {
            corrII[i] = guide[i] * guide[i];
            corrIP[i] = guide[i] * input[i];
        }
        foreach (var plane in new[] { meanI, meanP, corrII, corrIP })
            Blur.Gauss(plane, width, height, sigma);

        var a = new float[n];
        var b = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
            {
                var variance = Math.Max(0, corrII[i] - meanI[i] * meanI[i]);
                var covariance = corrIP[i] - meanI[i] * meanP[i];
                a[i] = covariance / (variance + eps);
                b[i] = meanP[i] - a[i] * meanI[i];
            }
        });
        Blur.Gauss(a, width, height, sigma);
        Blur.Gauss(b, width, height, sigma);

        var result = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
                result[i] = a[i] * guide[i] + b[i];
        });
        return result;
    }
}
