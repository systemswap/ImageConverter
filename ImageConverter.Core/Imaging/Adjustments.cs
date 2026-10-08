namespace ImageConverter.Core.Imaging;

/// <summary>EinfÃ¤rben des ganzen Bildes (Kachel â€žToolsâ€œ).</summary>
public enum Tint
{
    None,
    BlackWhite,
    Sepia,
    Cool,
}

/// <summary>
/// Licht und Farbe aus der Kachel â€žToolsâ€œ. Jeder Regler lÃ¤uft von âˆ’100 bis +100,
/// 0 heiÃŸt unverÃ¤ndert.
/// </summary>
public sealed record Adjustments(
    double Brightness = 0,
    double Contrast = 0,
    double Shadows = 0,
    double Highlights = 0,
    double Saturation = 0,
    Tint Tint = Tint.None)
{
    public static readonly Adjustments None = new();

    public bool IsNone => this == None;
}

/// <summary>
/// Wendet <see cref="Adjustments"/> auf 8-Bit-BGRA-Pixel an â€” wie <see cref="Sharpen"/>
/// fÃ¼r Vorschau und Export dieselbe Funktion, damit man exportiert, was man gesehen hat.
///
/// Reihenfolge: Tiefen/Lichter â†’ Helligkeit â†’ Kontrast â†’ SÃ¤ttigung â†’ FÃ¤rben.
/// Tiefen und Lichter wirken lokal wie in Referenzwerkzeug: Ob eine Stelle als dunkel oder hell
/// gilt, entscheidet ihre Umgebung (kantenerhaltend geglÃ¤ttet), nicht das einzelne Pixel. Ein dunkles
/// Detail in einer hellen FlÃ¤che bleibt so dunkel, und das Bild wird nicht flau.
/// </summary>
public static class Adjust
{
    /// <summary>Radius der Umgebung fÃ¼r Tiefen/Lichter, relativ zur lÃ¤ngsten Kante â€”
    /// so wirkt es in der kleinen Vorschau und im Export gleich.</summary>
    private const double NeighbourhoodPerEdge = 0.01;

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
        var curve = ToneCurve(a.Brightness / 100, a.Contrast / 100);

        // Umgebungshelligkeit nur, wenn Tiefen oder Lichter gebraucht werden
        float[]? surround = null;
        if (shadows != 0 || highlights != 0)
        {
            surround = new float[pixels];
            Parallel.For(0, height, y =>
            {
                for (int x = 0, p = y * width; x < width; x++, p++)
                    surround[p] = (Kb * bgra[p * 4] + Kg * bgra[p * 4 + 1] + Kr * bgra[p * 4 + 2]) / 255f;
            });
            var sigma = Math.Max(1.0, NeighbourhoodPerEdge * Math.Max(width, height));
            surround = GuidedSmooth(surround, width, height, sigma);
        }

        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;

                if (surround is not null)
                {
                    var lum = Kr * r + Kg * g + Kb * b;
                    var target = ShadowsHighlights(lum, surround[p], shadows, highlights);
                    if (lum > 1f / 512)
                    {
                        // Farbton behalten: alle KanÃ¤le im selben VerhÃ¤ltnis
                        var f = Math.Min(target / lum, 8f);
                        r *= f; g *= f; b *= f;
                    }
                    else
                    {
                        r = g = b = target;   // fast schwarz: kein Farbton zu erhalten
                    }
                }

                r = curve[Index(r)]; g = curve[Index(g)]; b = curve[Index(b)];

                if (saturation != 0 || a.Tint != Tint.None)
                {
                    var lum = Kr * r + Kg * g + Kb * b;
                    if (saturation != 0)
                    {
                        var s = 1 + saturation;
                        r = lum + (r - lum) * s;
                        g = lum + (g - lum) * s;
                        b = lum + (b - lum) * s;
                    }
                    if (a.Tint != Tint.None)
                        (r, g, b) = Colorize(Math.Clamp(lum, 0, 1), a.Tint);
                }

                result[i] = ToByte(b);
                result[i + 1] = ToByte(g);
                result[i + 2] = ToByte(r);
            }
        });

        return result;
    }

    /// <summary>
    /// Zielhelligkeit eines Pixels. Tiefen hebt (oder senkt) dort, wo die Umgebung dunkel
    /// ist, Lichter dort, wo sie hell ist â€” als Belichtung in Blendenstufen, bei den
    /// Lichtern von WeiÃŸ aus gerechnet, damit nichts ausfrisst.
    /// </summary>
    /// <summary>Wirkung bei Â±100: zwei Blendenstufen.</summary>
    private const float MaxStops = 2f;

    internal static float ShadowsHighlights(float lum, float surround, float shadows, float highlights)
    {
        var result = lum;
        if (shadows != 0)
        {
            var w = 1 - SmoothStep(0f, 0.6f, surround);
            result *= MathF.Pow(2, MaxStops * shadows * w);
        }
        if (highlights != 0)
        {
            var w = SmoothStep(0.4f, 1f, surround);
            result = 1 - (1 - result) * MathF.Pow(2, -MaxStops * highlights * w);
        }
        return Math.Clamp(result, 0, 1);
    }

    /// <summary>
    /// Kantenerhaltendes GlÃ¤tten der Helligkeit (Guided Filter, He et al. 2010, mit sich
    /// selbst als FÃ¼hrung). FlÃ¤chen werden zur Umgebungshelligkeit gemittelt, an harten
    /// Kanten folgt das Ergebnis der Kante. Ein einfacher GauÃŸ blutete Ã¼ber die Kante:
    /// Himmel direkt an dunklen Bergen galt als â€ždunkle Umgebungâ€œ und bekam einen hellen
    /// Lichthof (Halo).
    /// </summary>
    internal static float[] GuidedSmooth(float[] lum, int width, int height, double sigma)
    {
        // Îµ: ab welcher Helligkeitsschwankung (als Varianz, Werte 0â€“1) eine Kante zÃ¤hlt
        const float eps = 0.003f;
        var n = lum.Length;

        var mean = (float[])lum.Clone();
        var meanSq = new float[n];
        for (var i = 0; i < n; i++) meanSq[i] = lum[i] * lum[i];
        Blur.Gauss(mean, width, height, sigma);
        Blur.Gauss(meanSq, width, height, sigma);

        // Pro Umgebung: lineares Modell q = aÂ·I + b
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

    private const int CurveSize = 4096;

    /// <summary>
    /// Helligkeit als Gamma auf die Mitten (Schwarz und WeiÃŸ bleiben), Kontrast als
    /// S-Kurve um die Mitte. Als Tabelle, damit der Export groÃŸer Bilder schnell bleibt.
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

    /// <summary>Verlauf Schwarz â†’ TÃ¶nung â†’ WeiÃŸ; die TÃ¶nung hat mittlere Helligkeit.</summary>
    private static (float R, float G, float B) Colorize(float lum, Tint tint)
    {
        var (mr, mg, mb) = tint switch
        {
            Tint.Sepia => (0.66f, 0.48f, 0.30f),
            Tint.Cool => (0.38f, 0.50f, 0.64f),
            _ => (0.5f, 0.5f, 0.5f),
        };
        if (tint == Tint.BlackWhite) return (lum, lum, lum);

        // Mitte so verschieben, dass die TÃ¶nung bei ihrer eigenen Helligkeit liegt
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

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    private static int Index(float v) => (int)(Math.Clamp(v, 0, 1) * (CurveSize - 1) + 0.5f);

    private static byte ToByte(float v) => (byte)(Math.Clamp(v, 0, 1) * 255 + 0.5f);
}
