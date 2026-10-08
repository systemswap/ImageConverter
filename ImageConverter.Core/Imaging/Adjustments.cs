namespace ImageConverter.Core.Imaging;

/// <summary>Einfärben des ganzen Bildes (Kachel „Tools“).</summary>
public enum Tint
{
    None,
    BlackWhite,
    /// <summary>Einfarbig im Farbton <see cref="Adjustments.TintHue"/>.</summary>
    Color,
}

/// <summary>
/// Licht und Farbe aus der Kachel „Tools“. Jeder Regler läuft von −100 bis +100,
/// 0 heißt unverändert; <see cref="TintHue"/> ist ein Farbwinkel 0–360°.
/// </summary>
public sealed record Adjustments(
    double Brightness = 0,
    double Contrast = 0,
    double Shadows = 0,
    double Highlights = 0,
    double Saturation = 0,
    Tint Tint = Tint.None,
    double Vibrance = 0,
    double TintHue = Adjustments.SepiaHue)
{
    /// <summary>Voreinstellung beim Färben: warmes Braun wie ein alter Fotoabzug.</summary>
    public const double SepiaHue = 30;

    public static readonly Adjustments None = new();

    /// <summary>Nichts zu tun — der Farbton zählt nur, wenn auch gefärbt wird.</summary>
    public bool IsNone => (Tint == Tint.Color ? this : this with { TintHue = SepiaHue }) == None;
}

/// <summary>
/// Wendet <see cref="Adjustments"/> auf 8-Bit-BGRA-Pixel an — wie <see cref="Sharpen"/>
/// für Vorschau und Export dieselbe Funktion, damit man exportiert, was man gesehen hat.
///
/// Reihenfolge: Tiefen/Lichter → Helligkeit → Kontrast → Dynamik → Sättigung → Färben.
/// Tiefen und Lichter wirken lokal wie in Referenzwerkzeug: Ob eine Stelle als dunkel oder hell
/// gilt, entscheidet ihre Umgebung (kantenerhaltend geglättet), nicht das einzelne Pixel. Ein dunkles
/// Detail in einer hellen Fläche bleibt so dunkel, und das Bild wird nicht flau.
/// </summary>
public static class Adjust
{
    /// <summary>Radius der Umgebung für Tiefen/Lichter, relativ zur längsten Kante —
    /// so wirkt es in der kleinen Vorschau und im Export gleich.</summary>
    private const double NeighbourhoodPerEdge = 0.01;

    /// <summary>Weitere Umgebung: Ein Bereich zählt nur als dunkel (bzw. hell), wenn auch
    /// sie es ist. Kleine dunkle Stellen in hellen Flächen — Pupillen, Nasenlöcher,
    /// Haarsträhnen im Gesicht — bleiben so unverändert.</summary>
    private const double WideNeighbourhoodPerEdge = 0.03;

    // Gewichte der Helligkeit (Rec. 709) auf den sRGB-Werten
    private const float Kr = 0.2126f, Kg = 0.7152f, Kb = 0.0722f;

    public static byte[] Apply(byte[] bgra, int width, int height, Adjustments a)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(bgra.Length, width * height * 4, nameof(bgra));

        var result = (byte[])bgra.Clone();
        if (a.IsNone) return result;

        var pixels = width * height;
        var shadows = (float)Math.Clamp(a.Shadows / 100, -1, 1);
        var highlights = (float)Math.Clamp(a.Highlights / 100, -1, 1);
        var saturation = (float)Math.Clamp(a.Saturation / 100, -1, 1);
        var vibrance = (float)Math.Clamp(a.Vibrance / 100, -1, 1);
        var curve = ToneCurve(a.Brightness / 100, a.Contrast / 100);

        // Umgebungshelligkeit nur, wenn Tiefen oder Lichter gebraucht werden
        float[]? dark = null, bright = null;
        if (shadows != 0 || highlights != 0)
        {
            var surround = new float[pixels];
            Parallel.For(0, height, y =>
            {
                for (int x = 0, p = y * width; x < width; x++, p++)
                    surround[p] = (Kb * bgra[p * 4] + Kg * bgra[p * 4 + 1] + Kr * bgra[p * 4 + 2]) / 255f;
            });
            var edge = Math.Max(width, height);
            var wide = (float[])surround.Clone();
            Blur.Gauss(wide, width, height, Math.Max(1.0, WideNeighbourhoodPerEdge * edge));
            surround = GuidedSmooth(surround, width, height, Math.Max(1.0, NeighbourhoodPerEdge * edge));

            // Tiefen: dunkel nur, wenn nah UND weit dunkel (der hellere Wert gilt).
            // Lichter: hell nur, wenn nah UND weit hell (der dunklere Wert gilt).
            dark = new float[pixels];
            bright = new float[pixels];
            var near = surround;
            Parallel.For(0, height, y =>
            {
                for (int x = 0, p = y * width; x < width; x++, p++)
                {
                    dark[p] = Math.Max(near[p], wide[p]);
                    bright[p] = Math.Min(near[p], wide[p]);
                }
            });
        }

        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;

                if (dark is not null && bright is not null)
                {
                    var lum = Kr * r + Kg * g + Kb * b;
                    var target = ShadowsHighlights(lum, dark[p], bright[p], shadows, highlights);
                    if (lum > 1f / 512)
                    {
                        // Farbton behalten: alle Kanäle im selben Verhältnis
                        var f = Math.Min(target / lum, 8f);
                        r *= f; g *= f; b *= f;
                    }
                    else
                    {
                        r = g = b = target;   // fast schwarz: kein Farbton zu erhalten
                    }
                }

                r = curve[Index(r)]; g = curve[Index(g)]; b = curve[Index(b)];

                if (vibrance != 0 || saturation != 0 || a.Tint != Tint.None)
                {
                    var lum = Kr * r + Kg * g + Kb * b;
                    if (vibrance != 0)
                    {
                        var s = VibranceFactor(r, g, b, vibrance);
                        r = lum + (r - lum) * s;
                        g = lum + (g - lum) * s;
                        b = lum + (b - lum) * s;
                    }
                    if (saturation != 0)
                    {
                        var s = 1 + saturation;
                        r = lum + (r - lum) * s;
                        g = lum + (g - lum) * s;
                        b = lum + (b - lum) * s;
                    }
                    if (a.Tint != Tint.None)
                        (r, g, b) = Colorize(Math.Clamp(lum, 0, 1), a.Tint, (float)a.TintHue);
                }

                result[i] = ToByte(b);
                result[i + 1] = ToByte(g);
                result[i + 2] = ToByte(r);
            }
        });

        return result;
    }

    /// <summary>Wirkung bei ±100: zwei Blendenstufen.</summary>
    private const float MaxStops = 2f;

    /// <summary>
    /// Zielhelligkeit eines Pixels. Tiefen hebt (oder senkt) dort, wo die Umgebung dunkel
    /// ist, Lichter dort, wo sie hell ist — als Belichtung in Blendenstufen, bei den
    /// Lichtern von Weiß aus gerechnet, damit nichts ausfrisst.
    /// </summary>
    /// <remarks>
    /// Die Bereiche sind bewusst schmal: Tiefen wirkt voll im wirklich Dunklen und endet
    /// bei Umgebungshelligkeit 0,35, Lichter beginnt erst bei 0,65. Gesichter in
    /// Innenräumen liegen typisch bei 0,4–0,6 und bleiben so unberührt (gemessen an einem
    /// Familienfoto: Flur 0,08, Türrahmen 0,15, Haare/Augenpartie 0,36–0,40, Wangen 0,6).
    /// Vorher reichte Tiefen bis 0,6 und hellte Gesichter merklich mit auf.
    /// </remarks>
    internal static float ShadowsHighlights(float lum, float darkSurround, float brightSurround, float shadows, float highlights)
    {
        var result = lum;
        if (shadows != 0)
        {
            var w = 1 - SmoothStep(0.02f, 0.35f, darkSurround);
            result *= MathF.Pow(2, MaxStops * shadows * w);
        }
        if (highlights != 0)
        {
            var w = SmoothStep(0.65f, 0.98f, brightSurround);
            result = 1 - (1 - result) * MathF.Pow(2, -MaxStops * highlights * w);
        }
        return Math.Clamp(result, 0, 1);
    }

    /// <summary>
    /// Kantenerhaltendes Glätten der Helligkeit (Guided Filter, He et al. 2010, mit sich
    /// selbst als Führung). Flächen werden zur Umgebungshelligkeit gemittelt, an harten
    /// Kanten folgt das Ergebnis der Kante. Ein einfacher Gauß blutete über die Kante:
    /// Himmel direkt an dunklen Bergen galt als „dunkle Umgebung“ und bekam einen hellen
    /// Lichthof (Halo).
    /// </summary>
    internal static float[] GuidedSmooth(float[] lum, int width, int height, double sigma)
    {
        // ε: ab welcher Helligkeitsschwankung (als Varianz, Werte 0–1) eine Kante zählt
        const float eps = 0.003f;
        var n = lum.Length;

        var mean = (float[])lum.Clone();
        var meanSq = new float[n];
        for (var i = 0; i < n; i++) meanSq[i] = lum[i] * lum[i];
        Blur.Gauss(mean, width, height, sigma);
        Blur.Gauss(meanSq, width, height, sigma);

        // Pro Umgebung: lineares Modell q = a·I + b
        var a = new float[n];
        var b = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
            {
                var variance = Math.Max(0, meanSq[i] - mean[i] * mean[i]);
                a[i] = variance / (variance + eps);
                b[i] = mean[i] - a[i] * mean[i];
            }
        });
        Blur.Gauss(a, width, height, sigma);
        Blur.Gauss(b, width, height, sigma);

        var result = new float[n];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, i = y * width; x < width; x++, i++)
                result[i] = a[i] * lum[i] + b[i];
        });
        return result;
    }

    /// <summary>
    /// Dynamik wie in Referenzwerkzeug: verstärkt blasse Farben stark, kräftige kaum und schont
    /// Hauttöne — Bilder werden lebendiger, Gesichter nicht orange. Nach links werden
    /// zuerst die blassen Farben grau, kräftige bleiben länger.
    /// </summary>
    /// <returns>Faktor für den Abstand jedes Kanals zur Helligkeit (1 = unverändert).</returns>
    internal static float VibranceFactor(float r, float g, float b, float vibrance)
    {
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var sat = max > 1e-3f ? Math.Clamp((max - min) / max, 0, 1) : 0;

        if (vibrance < 0)
            return Math.Max(0, 1 + vibrance * (1 - 0.6f * sat));

        var room = MathF.Pow(1 - sat, 1.5f);                  // blass: viel Spielraum
        return 1 + vibrance * room * (1 - 0.7f * SkinTone(r, g, b, sat));
    }

    /// <summary>
    /// Wie sehr ein Farbton nach Haut aussieht (0–1): Rot vor Grün vor Blau, Farbwinkel um
    /// 25° (orange-rötlich), mäßig gesättigt.
    /// </summary>
    private static float SkinTone(float r, float g, float b, float sat)
    {
        if (!(r >= g && g >= b) || r - b < 1e-3f) return 0;
        var hue = 60 * (g - b) / (r - b);                     // 0° rot … 60° gelb
        var onHue = Math.Clamp(1 - MathF.Abs(hue - 25) / 20, 0, 1);
        var onSat = 1 - SmoothStep(0.55f, 0.8f, sat);        // sehr kräftig = keine Haut
        return onHue * onSat;
    }

    private const int CurveSize = 4096;

    /// <summary>
    /// Helligkeit als Gamma auf die Mitten (Schwarz und Weiß bleiben), Kontrast als
    /// S-Kurve um die Mitte. Als Tabelle, damit der Export großer Bilder schnell bleibt.
    /// </summary>
    internal static float[] ToneCurve(double brightness, double contrast)
    {
        var gamma = Math.Pow(2, -0.8 * Math.Clamp(brightness, -1, 1));
        var p = Math.Pow(2, 1.2 * Math.Clamp(contrast, -1, 1));
        var curve = new float[CurveSize];
        for (var i = 0; i < CurveSize; i++)
        {
            var x = Math.Pow((double)i / (CurveSize - 1), gamma);
            x = x < 0.5 ? 0.5 * Math.Pow(2 * x, p) : 1 - 0.5 * Math.Pow(2 - 2 * x, p);
            curve[i] = (float)x;
        }
        return curve;
    }

    /// <summary>
    /// Verlauf Schwarz → Tönung → Weiß. Die Tönung ist eine gedämpfte Farbe im gewählten
    /// Farbton (HSL mit S 0,38, L 0,48 — bei 30° das klassische Sepia, bei 210° kühles Blau).
    /// </summary>
    private static (float R, float G, float B) Colorize(float lum, Tint tint, float hue)
    {
        if (tint == Tint.BlackWhite) return (lum, lum, lum);
        var (mr, mg, mb) = Hsl(hue, 0.38f, 0.48f);

        // Mitte so verschieben, dass die Tönung bei ihrer eigenen Helligkeit liegt
        var mid = Kr * mr + Kg * mg + Kb * mb;
        if (lum <= mid)
        {
            var t = lum / mid;
            return (mr * t, mg * t, mb * t);
        }
        else
        {
            var t = (lum - mid) / (1 - mid);
            return (mr + (1 - mr) * t, mg + (1 - mg) * t, mb + (1 - mb) * t);
        }
    }

    /// <summary>HSL → RGB, Farbwinkel in Grad, S und L 0–1.</summary>
    internal static (float R, float G, float B) Hsl(float hue, float s, float l)
    {
        var h = ((hue % 360) + 360) % 360 / 60f;
        var c = (1 - MathF.Abs(2 * l - 1)) * s;
        var x = c * (1 - MathF.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x),
        };
        var m = l - c / 2;
        return (r + m, g + m, b + m);
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static int Index(float v) => (int)(Math.Clamp(v, 0, 1) * (CurveSize - 1) + 0.5f);

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0, 1) * 255 + 0.5f);
}
