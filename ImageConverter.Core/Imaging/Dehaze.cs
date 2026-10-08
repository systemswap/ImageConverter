namespace ImageConverter.Core.Imaging;

/// <summary>
/// Dunst entfernen nach dem physikalischen Streumodell, in linearem Licht:
/// Bild = Szene · t + Luftlicht · (1 − t), t = Durchlässigkeit der Luft.
///
/// Aufbau nach dem Dark Channel Prior (He, Sun, Tang 2009/2011) mit den Verbesserungen,
/// die Adobe in seinem Patent zu „Dunst entfernen“ beschreibt (US 9 305 339 B2) und die
/// in der Literatur gegen die bekannten Schwächen helfen:
/// <list type="number">
/// <item>Rechnen in linearem Licht — das Modell gilt für Lichtmengen, nicht für
///   gamma-kodierte Werte (sonst Farbverschiebungen, falsche Helligkeit).</item>
/// <item>Luftlicht: hellste 1 % des dunklen Kanals, davon die mittleren (Adobe: Median) —
///   die Sonne im Bild bestimmt es so nicht.</item>
/// <item>Durchlässigkeit aus dem dunklen Kanal auf einer verkleinerten Fassung, verfeinert
///   mit einem Guided Filter mit farbigem Führungsbild und kantentreu auf volle Größe
///   gebracht (Fast Guided Filter, He 2015).</item>
/// <item>Toleranz: Was dem Luftlicht ähnlich ist — Himmel, weiße Wände —, gilt nicht als
///   dichter Dunst (sonst Farbstiche und Rauschen im Himmel).</item>
/// <item>Helligkeitsausgleich wie bei Adobe: großräumig an das Original angeglichen, nur
///   lokale Kontraste und Farben gewinnen — sonst wird das Bild dunkel.</item>
/// </list>
/// </summary>
internal static class Dehaze
{
    /// <summary>Längste Kante der Fassung, auf der Luftlicht und Durchlässigkeit geschätzt werden.</summary>
    private const int WorkEdge = 1024;

    /// <summary>Fenster des dunklen Kanals (Radius), relativ zur längsten Kante.</summary>
    private const double PatchPerEdge = 0.012;

    /// <summary>Guided Filter für die Durchlässigkeit (Radius als Gauß-Sigma), relativ.</summary>
    private const double RefinePerEdge = 0.025;

    /// <summary>Helligkeitsausgleich: Glättung der Helligkeit (großräumig), relativ.</summary>
    private const double BrightnessPerEdge = 0.05;

    /// <summary>Anteil des Helligkeitsausgleichs (1 = voll ans Original).</summary>
    private const float BrightnessMatch = 0.85f;

    /// <summary>Toleranz (lineares Licht): Farben näher am Luftlicht gelten nicht als dichter Dunst.</summary>
    private const float Tolerance = 0.08f;

    /// <summary>Kleinste Durchlässigkeit: begrenzt die Verstärkung.</summary>
    private const float MinTransmission = 0.1f;

    /// <param name="amount">−1 … +1: rechts Dunst entfernen, links Dunst hinzufügen.</param>
    public static byte[] Apply(byte[] bgra, int width, int height, float amount)
    {
        if (amount == 0) return (byte[])bgra.Clone();
        var strength = Math.Abs(amount);

        // Alle Schätzungen auf einer verkleinerten Fassung (Flächenmittel in linearem Licht);
        // das große Bild wird danach nur einmal durchlaufen — Panoramen haben 80 MP, eine
        // Kopie in Fließkomma bräuchte Gigabytes.
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)WorkEdge));
        var (small, sw, sh) = DownscaleLinear(bgra, width, height, f);
        var air = AirLight(small, sw, sh);
        var t = Transmission(small, sw, sh, air, 0.95f);
        ProtectSky(small, sw, sh, air, t);
        var guide = Encode(small);
        var refine = Math.Max(1.0, RefinePerEdge * Math.Max(sw, sh));
        var (ta, tb) = GuidedColor.Coefficients(guide, t, sw, sh, refine, eps: 1e-3f);

        // Helligkeitsausgleich: Verhältnis Original/Ergebnis auf der kleinen Fassung
        float[]? ga = null, gb = null;
        if (amount > 0)
        {
            var after = new float[small.Length];
            for (var p = 0; p < sw * sh; p++)
            {
                var tt = Transmit(ta, tb, sw, sh, p % sw, p / sw, guide[p * 3], guide[p * 3 + 1], guide[p * 3 + 2], small, p, air);
                for (var c = 0; c < 3; c++) after[p * 3 + c] = Recover(small[p * 3 + c], air[c], tt, strength);
            }
            (ga, gb) = BrightnessGain(small, after, guide, sw, sh);
        }
        var k = BrightnessMatch * strength;

        var result = new byte[bgra.Length];
        Parallel.For(0, height, y =>
        {
            var sy = (y + 0.5f) / f - 0.5f;
            Span<float> lin = stackalloc float[3];
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var sx = (x + 0.5f) / f - 0.5f;
                var i = p * 4;
                float gB = bgra[i] / 255f, gG = bgra[i + 1] / 255f, gR = bgra[i + 2] / 255f;   // Führungsbild = Original
                for (var c = 0; c < 3; c++) lin[c] = Srgb.ToLinear(bgra[i + c]);

                var tt = Math.Clamp(At(ta, sw, sh, sx, sy, 0) * gB + At(ta, sw, sh, sx, sy, 1) * gG
                                  + At(ta, sw, sh, sx, sy, 2) * gR + At(tb, sw, sh, sx, sy, 0, 1), 0, 1);
                tt = Tolerate(tt, lin[0], lin[1], lin[2], air);

                float oB, oG, oR;
                if (amount > 0)
                {
                    oB = Recover(lin[0], air[0], tt, strength);
                    oG = Recover(lin[1], air[1], tt, strength);
                    oR = Recover(lin[2], air[2], tt, strength);

                    // Helligkeit großräumig ans Original — aber nie heller als das Original
                    // (sonst heller Streifen über dem Horizont)
                    var lg = At(ga!, sw, sh, sx, sy, 0) * gB + At(ga!, sw, sh, sx, sy, 1) * gG
                           + At(ga!, sw, sh, sx, sy, 2) * gR + At(gb!, sw, sh, sx, sy, 0, 1);
                    var g = MathF.Exp(Math.Clamp(lg, -0.7f, 1.1f) * k);
                    if (g > 1)
                    {
                        var before = Luma(lin[0], lin[1], lin[2]);
                        g = Math.Min(g, Math.Max(1, before / Math.Max(Luma(oB, oG, oR), 1e-4f)));
                    }
                    oB *= g; oG *= g; oR *= g;
                }
                else
                {
                    // Dunst hinzufügen: Grundschleier plus mehr, wo es schon dunstig/fern ist
                    var add = strength * (0.3f + 0.5f * (1 - tt));
                    oB = lin[0] + (air[0] - lin[0]) * add;
                    oG = lin[1] + (air[1] - lin[1]) * add;
                    oR = lin[2] + (air[2] - lin[2]) * add;
                }

                result[i] = Srgb.ToByte(oB);
                result[i + 1] = Srgb.ToByte(oG);
                result[i + 2] = Srgb.ToByte(oR);
                result[i + 3] = bgra[i + 3];
            }
        });
        return result;
    }

    /// <summary>Szene zurückrechnen, mit der Stärke zwischen Original und voller Wirkung.</summary>
    private static float Recover(float v, float air, float t, float strength) =>
        v + ((v - air) / t + air - v) * strength;

    /// <summary>
    /// Toleranz: Was dem Luftlicht sehr ähnlich ist (Himmel am Horizont, weiße Wände), gilt
    /// nicht als dichter Dunst — sonst Farbstiche und Rauschen. Dazu die Untergrenze.
    /// </summary>
    private static float Tolerate(float t, float b, float g, float r, float[] air)
    {
        var dist = Math.Max(Math.Abs(b - air[0]), Math.Max(Math.Abs(g - air[1]), Math.Abs(r - air[2])));
        if (dist < Tolerance) t = Math.Min(1, t * Tolerance / Math.Max(dist, 1e-4f));
        return Math.Clamp(t, MinTransmission, 1);
    }

    /// <summary>Durchlässigkeit an einem Pixel der kleinen Fassung (für den Helligkeitsausgleich).</summary>
    private static float Transmit(float[] ta, float[] tb, int sw, int sh, int x, int y,
        float gB, float gG, float gR, float[] lin, int p, float[] air)
    {
        var t = Math.Clamp(At(ta, sw, sh, x, y, 0) * gB + At(ta, sw, sh, x, y, 1) * gG
                         + At(ta, sw, sh, x, y, 2) * gR + At(tb, sw, sh, x, y, 0, 1), 0, 1);
        return Tolerate(t, lin[p * 3], lin[p * 3 + 1], lin[p * 3 + 2], air);
    }

    /// <summary>
    /// Durchlässigkeit t = 1 − ω · dunkler Kanal des aufs Luftlicht normierten Bildes.
    /// </summary>
    internal static float[] Transmission(float[] lin, int w, int h, float[] air, float omega)
    {
        var n = w * h;
        var minRgb = new float[n];
        for (var p = 0; p < n; p++)
            minRgb[p] = Math.Min(lin[p * 3] / air[0], Math.Min(lin[p * 3 + 1] / air[1], lin[p * 3 + 2] / air[2]));
        var dark = MinFilter(minRgb, w, h, Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(w, h))));
        var t = new float[n];
        for (var p = 0; p < n; p++) t[p] = 1 - omega * dark[p];
        return t;
    }

    /// <summary>
    /// Luftlicht (B, G, R, linear): unter den Pixeln, deren dunkler Kanal zu den hellsten 1 %
    /// gehört, die mittleren 20 % gemittelt (Adobe: Median). Die Sonne oder Lichter
    /// bestimmen es so nicht. Mindestens 0,25 (linear), damit dunkle Bilder ohne Dunst nicht
    /// kaputtgerechnet werden.
    /// </summary>
    internal static float[] AirLight(float[] lin, int w, int h)
    {
        var n = w * h;
        var minRgb = new float[n];
        for (var p = 0; p < n; p++) minRgb[p] = Math.Min(lin[p * 3], Math.Min(lin[p * 3 + 1], lin[p * 3 + 2]));
        var dark = MinFilter(minRgb, w, h, Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(w, h))));

        var top = Enumerable.Range(0, n).OrderByDescending(p => dark[p]).Take(Math.Max(1, n / 100)).ToArray();
        var from = (int)(top.Length * 0.4);
        var count = Math.Max(1, (int)(top.Length * 0.2));
        var air = new float[3];
        foreach (var p in top.Skip(from).Take(count))
            for (var c = 0; c < 3; c++) air[c] += lin[p * 3 + c];
        for (var c = 0; c < 3; c++) air[c] = Math.Max(0.25f, air[c] / count);
        return air;
    }

    /// <summary>
    /// Himmel schützen: Flächen ohne Struktur, nicht dunkel, sind Himmel, kein Dunst vor einer
    /// Landschaft. Ohne Schutz rechnete der dunkle Kanal den Himmel als „Dunst“ heraus — er
    /// kippte ins Türkis oder bekam neben der Sonne einen violetten Stich. Ein Rest Wirkung
    /// bleibt (wie in Referenzwerkzeug wird er etwas blauer).
    /// </summary>
    /// <remarks>
    /// Struktur als Differenz zweier unterschiedlich stark geglätteter Fassungen (Bandpass):
    /// gleichmäßige Verläufe — Himmel zur Sonne hin — heben sich darin auf, Wiesen, Bäume und
    /// Felder nicht, auch nicht im dichten Nebel. Gemessen (relativ zum Mittel): Himmel
    /// 0,002–0,005, Landschaft 0,04–0,38, dichter Testnebel 0,03. Die einfache
    /// Helligkeitsschwankung taugte nicht: Ein Himmel mit Verlauf schwankt auch, und Nebel
    /// drückt die Schwankung der Landschaft — dichter Nebel galt als Himmel.
    /// </remarks>
    private static void ProtectSky(float[] lin, int w, int h, float[] air, float[] t)
    {
        var l = Luma(lin);
        var edge = Math.Max(w, h);
        var fine = (float[])l.Clone();
        var coarse = (float[])l.Clone();
        Blur.Gauss(fine, w, h, Math.Max(0.6, 0.002 * edge));
        Blur.Gauss(coarse, w, h, Math.Max(1.5, 0.006 * edge));
        var band = new float[l.Length];
        for (var i = 0; i < band.Length; i++) band[i] = Math.Abs(fine[i] - coarse[i]);
        var mean = (float[])l.Clone();
        var sigma = Math.Max(1.0, PatchPerEdge * edge);
        Blur.Gauss(band, w, h, sigma);
        Blur.Gauss(mean, w, h, sigma);

        for (var i = 0; i < t.Length; i++)
        {
            var structure = band[i] / Math.Max(mean[i], 1e-3f);
            var smooth = 1 - SmoothStep(0.008f, 0.02f, structure);
            var bright = SmoothStep(0.03f, 0.1f, l[i]);   // dunkle glatte Schatten sind kein Himmel
            t[i] += (1 - t[i]) * SkyProtection * bright * smooth;
        }
    }

    /// <summary>Anteil, um den der Himmel geschützt wird (1 = gar nicht verändert).</summary>
    private const float SkyProtection = 0.85f;

    /// <summary>
    /// Helligkeitsausgleich (Adobe: „guided brightness scaling“): Die großräumige Helligkeit
    /// des Ergebnisses wird an die des Originals angeglichen; lokale Kontraste und Farben
    /// bleiben gewonnen. Das Verhältnis wird mit dem farbigen Guided Filter geglättet — ein
    /// einfacher Weichzeichner trug die Aufhellung vom Land über den Horizont in den Himmel
    /// (heller Streifen). Faktor begrenzt auf 0,5 … 3.
    /// </summary>
    /// <returns>Koeffizienten für log(Faktor), anzuwenden mit dem Original als Führungsbild.</returns>
    private static (float[] A, float[] B) BrightnessGain(float[] before, float[] after, float[] guide, int sw, int sh)
    {
        var logGain = new float[sw * sh];
        for (var p = 0; p < logGain.Length; p++)
        {
            var lb = Luma(before[p * 3], before[p * 3 + 1], before[p * 3 + 2]);
            var la = Luma(after[p * 3], after[p * 3 + 1], after[p * 3 + 2]);
            logGain[p] = MathF.Log(Math.Clamp(lb / Math.Max(la, 1e-4f), 0.5f, 3f));
        }
        return GuidedColor.Coefficients(guide, logGain, sw, sh,
            Math.Max(1.0, BrightnessPerEdge * Math.Max(sw, sh)), eps: 1e-2f);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static float[] Luma(float[] lin)
    {
        var n = lin.Length / 3;
        var l = new float[n];
        for (var p = 0; p < n; p++) l[p] = 0.0722f * lin[p * 3] + 0.7152f * lin[p * 3 + 1] + 0.2126f * lin[p * 3 + 2];
        return l;
    }

    private static float[] Encode(float[] lin)
    {
        var g = new float[lin.Length];
        for (var i = 0; i < g.Length; i++) g[i] = Srgb.Encode(lin[i]);
        return g;
    }

    /// <summary>
    /// Verkleinern direkt aus den 8-Bit-Pixeln: Flächenmittel über f × f Blöcke in linearem
    /// Licht (physikalisch richtig). Ergebnis B, G, R je Pixel.
    /// </summary>
    private static (float[] Data, int W, int H) DownscaleLinear(byte[] bgra, int w, int h, int f)
    {
        int sw = (w + f - 1) / f, sh = (h + f - 1) / f;
        var dst = new float[sw * sh * 3];
        Parallel.For(0, sh, sy =>
        {
            for (var sx = 0; sx < sw; sx++)
            {
                float b = 0, g = 0, r = 0;
                var count = 0;
                for (var y = sy * f; y < Math.Min(h, sy * f + f); y++)
                for (var x = sx * f; x < Math.Min(w, sx * f + f); x++)
                {
                    var i = (y * w + x) * 4;
                    b += Srgb.ToLinear(bgra[i]); g += Srgb.ToLinear(bgra[i + 1]); r += Srgb.ToLinear(bgra[i + 2]);
                    count++;
                }
                var o = (sy * sw + sx) * 3;
                (dst[o], dst[o + 1], dst[o + 2]) = (b / count, g / count, r / count);
            }
        });
        return (dst, sw, sh);
    }

    private static float Luma(float b, float g, float r) => 0.0722f * b + 0.7152f * g + 0.2126f * r;

    /// <summary>Bilinear aus einer Ebene mit <paramref name="ch"/> Kanälen, Kanal <paramref name="c"/>.</summary>
    private static float At(float[] data, int w, int h, float x, float y, int c, int ch = 3)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        float fx = x - x0, fy = y - y0;
        var top = data[(y0 * w + x0) * ch + c] * (1 - fx) + data[(y0 * w + x1) * ch + c] * fx;
        var bottom = data[(y1 * w + x0) * ch + c] * (1 - fx) + data[(y1 * w + x1) * ch + c] * fx;
        return top * (1 - fy) + bottom * fy;
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
}

/// <summary>sRGB ↔ linear (Tabellen für 8 Bit, Formel für die Rückrichtung).</summary>
internal static class Srgb
{
    private static readonly float[] ToLinearTable = Enumerable.Range(0, 256).Select(i =>
    {
        var v = i / 255.0;
        return (float)(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4));
    }).ToArray();

    public static float ToLinear(byte v) => ToLinearTable[v];

    // Rückrichtung als Tabelle mit linearer Zwischenwertbildung — Panoramen haben 80 MP
    private const int EncodeSize = 8192;
    private static readonly float[] EncodeTable = Enumerable.Range(0, EncodeSize + 1).Select(i =>
    {
        var v = (double)i / EncodeSize;
        return (float)(v <= 0.0031308 ? 12.92 * v : 1.055 * Math.Pow(v, 1 / 2.4) - 0.055);
    }).ToArray();

    public static float Encode(float lin)
    {
        var x = Math.Clamp(lin, 0, 1) * EncodeSize;
        var i = (int)x;
        if (i >= EncodeSize) return EncodeTable[EncodeSize];
        // Ganz unten ist die Kurve steil — dort genau rechnen
        if (i < 8) return Math.Clamp(lin, 0, 1) <= 0.0031308f ? 12.92f * Math.Clamp(lin, 0, 1) : 1.055f * MathF.Pow(lin, 1 / 2.4f) - 0.055f;
        return EncodeTable[i] + (EncodeTable[i + 1] - EncodeTable[i]) * (x - i);
    }

    public static byte ToByte(float lin) => (byte)(Encode(lin) * 255 + 0.5f);
}

/// <summary>
/// Guided Filter mit farbigem Führungsbild (He, Sun, Tang 2010): pro Umgebung das lineare
/// Modell q = aᵀ·I + b mit I = (B, G, R). Kantentreuer als mit einem grauen Führungsbild —
/// Farbkanten (grüner Wald vor blauem Himmel gleicher Helligkeit) bleiben erhalten.
/// Liefert die geglätteten Koeffizienten, damit sie auf einer größeren Fassung angewandt
/// werden können (Fast Guided Filter).
/// </summary>
internal static class GuidedColor
{
    /// <returns>a (3 Kanäle je Pixel) und b (1 Kanal).</returns>
    public static (float[] A, float[] B) Coefficients(float[] guide, float[] input, int w, int h, double sigma, float eps)
    {
        var n = w * h;
        float[] Plane(Func<int, float> f) { var a = new float[n]; for (var i = 0; i < n; i++) a[i] = f(i); Blur.Gauss(a, w, h, sigma); return a; }

        var mB = Plane(i => guide[i * 3]);
        var mG = Plane(i => guide[i * 3 + 1]);
        var mR = Plane(i => guide[i * 3 + 2]);
        var mP = Plane(i => input[i]);
        var bb = Plane(i => guide[i * 3] * guide[i * 3]);
        var bg = Plane(i => guide[i * 3] * guide[i * 3 + 1]);
        var br = Plane(i => guide[i * 3] * guide[i * 3 + 2]);
        var gg = Plane(i => guide[i * 3 + 1] * guide[i * 3 + 1]);
        var gr = Plane(i => guide[i * 3 + 1] * guide[i * 3 + 2]);
        var rr = Plane(i => guide[i * 3 + 2] * guide[i * 3 + 2]);
        var pB = Plane(i => guide[i * 3] * input[i]);
        var pG = Plane(i => guide[i * 3 + 1] * input[i]);
        var pR = Plane(i => guide[i * 3 + 2] * input[i]);

        var a = new float[n * 3];
        var b = new float[n];
        Parallel.For(0, n, i =>
        {
            // Kovarianz Σ (+ ε·E) und Kovarianz mit der Eingabe
            double s11 = bb[i] - mB[i] * mB[i] + eps, s12 = bg[i] - mB[i] * mG[i], s13 = br[i] - mB[i] * mR[i];
            double s22 = gg[i] - mG[i] * mG[i] + eps, s23 = gr[i] - mG[i] * mR[i];
            double s33 = rr[i] - mR[i] * mR[i] + eps;
            double c1 = pB[i] - mB[i] * mP[i], c2 = pG[i] - mG[i] * mP[i], c3 = pR[i] - mR[i] * mP[i];

            // a = Σ⁻¹ · c (symmetrische 3×3-Matrix, Adjunkte)
            double i11 = s22 * s33 - s23 * s23, i12 = s13 * s23 - s12 * s33, i13 = s12 * s23 - s13 * s22;
            double i22 = s11 * s33 - s13 * s13, i23 = s13 * s12 - s11 * s23, i33 = s11 * s22 - s12 * s12;
            var det = s11 * i11 + s12 * i12 + s13 * i13;
            if (Math.Abs(det) < 1e-12) det = 1e-12;
            var a1 = (i11 * c1 + i12 * c2 + i13 * c3) / det;
            var a2 = (i12 * c1 + i22 * c2 + i23 * c3) / det;
            var a3 = (i13 * c1 + i23 * c2 + i33 * c3) / det;
            a[i * 3] = (float)a1; a[i * 3 + 1] = (float)a2; a[i * 3 + 2] = (float)a3;
            b[i] = (float)(mP[i] - a1 * mB[i] - a2 * mG[i] - a3 * mR[i]);
        });

        // Koeffizienten glätten (jedes Pixel liegt in vielen Fenstern)
        var aB = new float[n]; var aG = new float[n]; var aR = new float[n];
        for (var i = 0; i < n; i++) { aB[i] = a[i * 3]; aG[i] = a[i * 3 + 1]; aR[i] = a[i * 3 + 2]; }
        foreach (var plane in new[] { aB, aG, aR, b }) Blur.Gauss(plane, w, h, sigma);
        for (var i = 0; i < n; i++) { a[i * 3] = aB[i]; a[i * 3 + 1] = aG[i]; a[i * 3 + 2] = aR[i]; }
        return (a, b);
    }
}
