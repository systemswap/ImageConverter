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
    double TintHue = Adjustments.SepiaHue,
    double Dehaze = 0,
    double MatchLuminance = 0,
    bool Neutralize = false)
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
/// Reihenfolge: Dunst → Tiefen/Lichter → Helligkeit → Kontrast → Gleiche Farbe (Ausgleichen,
/// Luminanz) → Dynamik → Sättigung → Färben.
/// Tiefen und Lichter wirken lokal wie in Referenzwerkzeug: Ob eine Stelle als dunkel oder hell
/// gilt, entscheidet ihre Umgebung auf jeder Größenordnung (Local Laplacian Filter, wie
/// Referenzwerkzeug), nicht das einzelne Pixel. Ein dunkles
/// Detail in einer hellen Fläche bleibt so dunkel, und das Bild wird nicht flau.
/// </summary>
public static class Adjust
{
    /// <summary>Weite Umgebung relativ zur längsten Kante: Ein Bereich zählt nur als dunkel
    /// (bzw. hell), wenn auch sie es ist. Kleine dunkle Stellen in hellen Flächen — Pupillen,
    /// Nasenlöcher, Haarsträhnen im Gesicht — bleiben so unverändert. Relativ, damit es in
    /// der kleinen Vorschau und im Export gleich wirkt.</summary>
    private const double WideNeighbourhoodPerEdge = 0.03;

    /// <summary>Ab diesem Helligkeitsabstand zur Umgebung gilt ein Übergang als Kante,
    /// über die Tiefen/Lichter nicht hinweg wirken.</summary>
    private const float EdgeContrast = 0.15f;

    // Gewichte der Helligkeit (Rec. 709) auf den sRGB-Werten
    private const float Kr = 0.2126f, Kg = 0.7152f, Kb = 0.0722f;

    public static byte[] Apply(byte[] bgra, int width, int height, Adjustments a) =>
        Apply(Prepare(bgra, width, height, a), width, height, a);

    /// <summary>Ergebnis des teuren Teils: Bild nach „Dunst entfernen“ und die Zielhelligkeit
    /// aus Tiefen/Lichtern (null, wenn beide auf 0).</summary>
    public sealed record Prepared(byte[] Source, float[]? Toned);

    /// <summary>
    /// Der teure Teil — Dunst entfernen und Tiefen/Lichter über den Local Laplacian Filter.
    /// Hängt nur von Bild, Dunst, Tiefen und Lichtern ab; die Vorschau merkt sich das
    /// Ergebnis, damit die übrigen Regler flüssig bleiben.
    /// </summary>
    public static Prepared Prepare(byte[] bgra, int width, int height, Adjustments a)
    {
        var dehaze = (float)Math.Clamp(a.Dehaze / 100, -1, 1);
        var source = dehaze != 0 ? Dehaze.Apply(bgra, width, height, dehaze) : bgra;
        return new Prepared(source, ToneMap(source, width, height, a));
    }

    private static float[]? ToneMap(byte[] bgra, int width, int height, Adjustments a)
    {
        var shadows = (float)Math.Clamp(a.Shadows / 100, -1, 1);
        var highlights = (float)Math.Clamp(a.Highlights / 100, -1, 1);
        return shadows != 0 || highlights != 0
            ? ShadowsHighlights(bgra, width, height, shadows, highlights)
            : null;
    }

    /// <param name="prepared">Ergebnis von <see cref="Prepare"/> für dasselbe Bild.</param>
    public static byte[] Apply(Prepared prepared, int width, int height, Adjustments a)
    {
        var (bgra, toned) = (prepared.Source, prepared.Toned);
        ArgumentOutOfRangeException.ThrowIfNotEqual(bgra.Length, width * height * 4, nameof(prepared));

        var result = (byte[])bgra.Clone();
        if (a.IsNone) return result;

        var saturation = (float)Math.Clamp(a.Saturation / 100, -1, 1);
        var vibrance = (float)Math.Clamp(a.Vibrance / 100, -1, 1);
        var curve = ToneCurve(a.Brightness / 100, a.Contrast / 100);
        var luminance = Math.Max(0.01f, 1 + (float)Math.Clamp(a.MatchLuminance / 100, -1, 1));   // wie Referenzwerkzeug 1–200 %
        var (castA, castB) = a.Neutralize ? ColourCast(bgra, width, height) : (0f, 0f);
        var oklab = vibrance != 0 || saturation != 0 || luminance != 1 || a.Neutralize;

        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                float b = bgra[i] / 255f, g = bgra[i + 1] / 255f, r = bgra[i + 2] / 255f;

                if (toned is not null)
                {
                    var lum = Kr * r + Kg * g + Kb * b;
                    var target = Math.Clamp(toned[p], 0, 1);
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

                if (oklab)
                {
                    // In Oklab: Buntheit ändern lässt Farbton und Helligkeit stehen
                    var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                    oa -= castA;                                       // Gleiche Farbe: Ausgleichen
                    ob -= castB;
                    var factor = 1f;
                    if (luminance != 1 && L > 1e-4f)
                    {
                        // Gleiche Farbe, Luminanz: wie mehr Licht — Helligkeit und Buntheit im
                        // selben Verhältnis (in Oklab exakt eine stärkere Beleuchtung). Nur die
                        // Helligkeit zu heben ließ die Farben blass wirken.
                        var lifted = Shoulder(L * luminance);
                        factor = lifted / L;
                        L = lifted;
                    }
                    if (vibrance != 0) factor *= VibranceFactor(L, oa, ob, vibrance);
                    if (saturation != 0) factor *= 1 + saturation;
                    (r, g, b) = Oklab.ScaleChroma(L, oa, ob, factor);
                }
                if (a.Tint != Tint.None)
                    (r, g, b) = Colorize(Math.Clamp(Kr * r + Kg * g + Kb * b, 0, 1), a.Tint, (float)a.TintHue);

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
    /// bei Umgebungshelligkeit 0,4 (kaum noch ab 0,35), Lichter beginnt erst bei 0,6. Gesichter in
    /// Innenräumen liegen typisch bei 0,4–0,6 und bleiben so unberührt (gemessen an einem
    /// Familienfoto: Flur 0,08, Türrahmen 0,15, Haare/Augenpartie 0,36–0,40, Wangen 0,6).
    /// Vorher reichte Tiefen bis 0,6 und hellte Gesichter merklich mit auf.
    /// </remarks>
    /// <summary>Bis hierher (von Schwarz bzw. Weiß aus) wirken Tiefen bzw. Lichter.</summary>
    private const float ToneReach = 0.4f;

    /// <summary>Stärke der Anhebung; ≤ 3 hält die Kurve monoton (Steigung ≥ 1 − Lift/3).</summary>
    private const float Lift = 2.5f;

    internal static float ShadowCurve(float x, float shadows) => shadows >= 0
        ? Math.Clamp(x + shadows * Raise(x), 0, 1)
        : Math.Clamp(x * MathF.Pow(2, MaxStops * shadows * (1 - SmoothStep(0.02f, ToneReach, x))), 0, 1);

    internal static float HighlightCurve(float x, float highlights) => highlights <= 0
        ? Math.Clamp(x - (-highlights) * Raise(1 - x), 0, 1)
        : Math.Clamp(1 - (1 - x) * MathF.Pow(2, -MaxStops * highlights * SmoothStep(1 - ToneReach, 0.98f, x)), 0, 1);

    /// <summary>
    /// Anhebung der Tiefen: x·(1 − x/Reichweite)²·Lift, null ab der Reichweite. Bewusst
    /// keine Belichtung (x·2^…): die kippte bei starken Werten — 0,25 wurde heller als 0,30,
    /// die Tonwerte drehten sich um.
    /// </summary>
    private static float Raise(float x)
    {
        if (x <= 0 || x >= ToneReach) return 0;
        var u = 1 - x / ToneReach;
        return Lift * x * u * u;
    }

    /// <summary>
    /// Zielhelligkeit je Pixel: Tiefen- und Lichter-Kurve über den Local Laplacian Filter,
    /// jeweils mit der weiten Umgebung als zweitem Maßstab (Tiefen: der hellere, Lichter:
    /// der dunklere der beiden Umgebungswerte).
    /// </summary>
    private static float[] ShadowsHighlights(byte[] bgra, int width, int height, float shadows, float highlights)
    {
        var lum = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
                lum[p] = (Kb * bgra[p * 4] + Kg * bgra[p * 4 + 1] + Kr * bgra[p * 4 + 2]) / 255f;
        });

        // Pyramidenstufe, deren Glättung etwa der weiten Umgebung entspricht (Stufe l ≈ 2^l px)
        var wideLevel = Math.Max(1, (int)Math.Round(Math.Log2(Math.Max(2, WideNeighbourhoodPerEdge * Math.Max(width, height)))));

        Func<Pyramid, int, float[]> Guide(Func<float, float, float> pick) => (p, l) =>
        {
            if (l >= wideLevel || wideLevel >= p.Count) return p[l];
            var near = p[l];
            var wide = p.UpsampledFrom(wideLevel, l);
            var g = new float[near.Length];
            for (var i = 0; i < g.Length; i++) g[i] = pick(near[i], wide[i]);
            return g;
        };

        if (shadows != 0)
        {
            var input = lum;
            lum = LocalLaplacian.Apply(input, width, height, s => ShadowCurve(s, shadows), EdgeContrast, Guide(Math.Max));
            // Was selbst hell ist, ändert Tiefen nicht stärker, als die Kurve es für diesen
            // Wert täte (also gar nicht) — sonst bleibt an harten Kanten ein heller Saum
            Bound(input, lum, s => ShadowCurve(s, shadows), v => SmoothStep(0.35f, 0.6f, v));
        }
        if (highlights != 0)
        {
            var input = lum;
            lum = LocalLaplacian.Apply(input, width, height, s => HighlightCurve(s, highlights), EdgeContrast, Guide(Math.Min));
            Bound(input, lum, s => HighlightCurve(s, highlights), v => 1 - SmoothStep(0.4f, 0.65f, v));
        }
        return lum;
    }

    /// <summary>
    /// Begrenzt die Änderung je Pixel auf die der Kurve für den eigenen Wert — mit Anteil
    /// <paramref name="strength"/> (0 = Filter frei, 1 = voll begrenzt).
    /// </summary>
    private static void Bound(float[] input, float[] output, Func<float, float> curve, Func<float, float> strength)
    {
        Parallel.For(0, (input.Length + 4095) / 4096, chunk =>
        {
            var end = Math.Min(input.Length, (chunk + 1) * 4096);
            for (var i = chunk * 4096; i < end; i++)
            {
                var s = strength(input[i]);
                if (s <= 0) continue;
                var point = curve(input[i]);
                var bounded = point >= input[i] ? Math.Min(output[i], point) : Math.Max(output[i], point);
                output[i] += (bounded - output[i]) * s;
            }
        });
    }

    /// <summary>
    /// Dynamik wie in Referenzwerkzeug: verstärkt blasse Farben stark, kräftige kaum und schont
    /// Hauttöne — Bilder werden lebendiger, Gesichter nicht orange. Nach links werden
    /// zuerst die blassen Farben grau, kräftige bleiben länger.
    /// </summary>
    /// <summary>
    /// „Gleiche Farbe“ (Referenzwerkzeug CS5, ohne Quellbild), Regler Luminanz: Helligkeit wird
    /// <em>multipliziert</em> wie in Referenzwerkzeug (Lab-L, 1–200 %) — die Kontrastverhältnisse
    /// bleiben, das Bild wirkt leuchtender als mit einem Gamma, das nur die Mitten anhebt.
    /// Über 0,8 läuft es weich gegen Weiß aus, damit nichts ausfrisst.
    /// </summary>
    private static float Shoulder(float l)
    {
        const float knee = 0.8f;
        return l <= knee ? Math.Max(0, l) : knee + (1 - knee) * (1 - MathF.Exp(-(l - knee) / (1 - knee)));
    }

    /// <summary>
    /// „Gleiche Farbe“, Ausgleichen: Farbstich als mittlere Oklab-Farbrichtung der nicht
    /// kräftig bunten, nicht zu dunklen/hellen Bildteile (Grauwelt-Annahme). Kräftige Farben
    /// — ein rotes T-Shirt, blauer Himmel — zählen nicht, sonst würden sie „ausgeglichen“.
    /// Rechnet auf jedem 4. Pixel jeder 4. Zeile, das reicht für einen Mittelwert.
    /// </summary>
    internal static (float A, float B) ColourCast(byte[] bgra, int width, int height)
    {
        double sa = 0, sb = 0, n = 0;
        for (var y = 0; y < height; y += 4)
        for (var x = 0; x < width; x += 4)
        {
            var i = (y * width + x) * 4;
            var (L, a, b) = Oklab.FromSrgb(bgra[i + 2] / 255f, bgra[i + 1] / 255f, bgra[i] / 255f);
            if (L < 0.15f || L > 0.97f || a * a + b * b > 0.1f * 0.1f) continue;
            sa += a; sb += b; n++;
        }
        return n > 0 ? ((float)(sa / n), (float)(sb / n)) : (0, 0);
    }

    /// <summary>Buntheit, ab der eine Farbe als kräftig gilt (Oklab-C; sRGB-Primärfarben ≈ 0,26–0,32).</summary>
    private const float StrongChroma = 0.2f;

    /// <returns>Faktor für die Buntheit in Oklab (1 = unverändert).</returns>
    internal static float VibranceFactor(float L, float a, float b, float vibrance)
    {
        var chroma = MathF.Sqrt(a * a + b * b);
        var sat = Math.Clamp(chroma / StrongChroma, 0, 1);

        if (vibrance < 0)
            return Math.Max(0, 1 + vibrance * (1 - 0.6f * sat));

        var room = MathF.Pow(1 - sat, 1.5f);                  // blass: viel Spielraum
        return 1 + 1.5f * vibrance * room * (1 - 0.75f * SkinTone(L, a, b, chroma));
    }

    /// <summary>
    /// Wie sehr eine Farbe nach Haut aussieht (0–1): Oklab-Farbwinkel um 55°
    /// (orange-rötlich), nicht zu dunkel, mäßig bunt — alle Hauttypen liegen auf dieser Achse.
    /// </summary>
    private static float SkinTone(float L, float a, float b, float chroma)
    {
        if (chroma < 1e-3f) return 0;
        var onHue = Math.Clamp(1 - MathF.Abs(Oklab.Hue(a, b) - 55) / 30, 0, 1);
        var onChroma = 1 - SmoothStep(0.12f, 0.18f, chroma);    // sehr kräftig = keine Haut
        var onLight = SmoothStep(0.25f, 0.4f, L);
        return onHue * onChroma * onLight;
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
