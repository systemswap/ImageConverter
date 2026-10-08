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

    // Vorgaben der Feineinstellung in Prozent (Regler in der Kachel Tools, Beta): die Werte, die
    // bis 0.4.40 fest eingebaut waren. Adjustments nimmt sie als Standard.
    /// <summary>Anteil des Helligkeitsausgleichs (100 = voll ans Original).</summary>
    internal const double DefaultBrightness = 85;
    /// <summary>Klarheit (lokaler Kontrast, großer Radius) bei Regler 100.</summary>
    internal const double DefaultClarity = 35;
    /// <summary>Feine Schärfe (3×3-Unscharfmaskierung) bei Regler 100.</summary>
    internal const double DefaultSharpen = 60;
    /// <summary>S-Kurve auf der Helligkeit bei Regler 100 (Steigung in der Mitte 1 + Wert).</summary>
    internal const double DefaultCurve = 12;
    /// <summary>Dynamik bei Regler 100: blasse Farben bis zu so viel mehr Buntheit.</summary>
    internal const double DefaultVibrance = 25;

    /// <summary>Höchstzahl der Durchläufe.</summary>
    internal const int MaxPasses = 10;

    // Schleife (Durchläufe > 1): Was die Regler je Durchlauf über die Tools-Kette bewirken, bei
    // Reglerstand 100 % — die Vorgaben (Kontrast 12, Dynamik 25, Schärfe 60) ergeben dann je Runde
    // Kontrast +8, Dynamik +8, feine Schärfe 0,3 wie die Laborschleife, die gefiel.
    /// <summary>Tools-Kontrast je Durchlauf bei 100 %.</summary>
    internal const double LoopContrast = 67;
    /// <summary>Tools-Dynamik je Durchlauf bei 100 %.</summary>
    internal const double LoopVibrance = 32;
    /// <summary>Feine Unscharfmaskierung je Durchlauf bei 100 %.</summary>
    internal const double LoopSharpen = 0.5;
    /// <summary>Gauß-Sigma der feinen Unscharfmaskierung in der Schleife.</summary>
    internal const double FineSigma = 0.7;
    /// <summary>Anteil des Kanten-Reglers je Durchlauf.</summary>
    internal const double LoopEdgeShare = 0.5;

    /// <summary>Kantenschärfe (Unscharfmaskierung nach allen Durchläufen): Gauß-Sigma in Pixeln.</summary>
    internal const double EdgeSigma = 2.0;

    /// <summary>Wahrnehmungsanteile als Faktoren (Prozent / 100); siehe die Default-Konstanten.</summary>
    internal sealed record Settings(float Clarity, float Sharpen, float Curve, float Vibrance, float BrightnessMatch)
    {
        public static readonly Settings Default = new(
            (float)(DefaultClarity / 100), (float)(DefaultSharpen / 100), (float)(DefaultCurve / 100),
            (float)(DefaultVibrance / 100), (float)(DefaultBrightness / 100));
    }

    /// <summary>Toleranz (lineares Licht): Farben näher am Luftlicht gelten nicht als dichter Dunst.</summary>
    private const float Tolerance = 0.08f;

    /// <summary>
    /// Buntheit: höchstens so viel mehr als im Original, je Einheit Regler, zusätzlich zu dem,
    /// was der Dunst genommen hat (1/t). Das Modell selbst gäbe bei dünnem Dunst viel mehr —
    /// es rechnet den dunkelsten Kanal gegen null, bei Grün ist das Blau: Felder wurden ocker
    /// und braun.
    /// </summary>
    private const float ChromaPerUnit = 0.15f;

    /// <summary>Radius der Klarheit (Gauß-Sigma des Guided Filters), relativ zur längsten Kante.</summary>
    private const double ClarityPerEdge = 0.02;

    /// <summary>Ab dieser Oklab-Buntheit wirkt die Dynamik nicht mehr (kräftige Farben bleiben).</summary>
    private const float VibranceFull = 0.12f;

    /// <summary>Kleinste Durchlässigkeit: begrenzt die Verstärkung.</summary>
    private const float MinTransmission = 0.1f;

    /// <param name="amount">−1 … +3: rechts Dunst entfernen (über 1 verstärkt), links Dunst hinzufügen.</param>
    public static byte[] Apply(byte[] bgra, int width, int height, float amount) =>
        Apply(bgra, width, height, amount, null, Settings.Default, share: 1f);

    /// <summary>Was ein Durchlauf an den nächsten weitergibt: Luftlichtfeld und Himmel der kleinen Fassung.</summary>
    internal sealed class Pass
    {
        public float[] Air = [];      // B, G, R je Pixel der kleinen Fassung
        public float[] Sky = [];      // Himmel je Pixel der kleinen Fassung (0 … 1)
        public int W, H;
    }

    /// <param name="pass">Durchläufe: Luftlicht und Himmel aus dem ersten weiterverwenden. Die Dunstmenge wird
    /// jedes Mal neu am Zwischenstand geschätzt — wo der Dunst weg ist, findet sie nichts mehr, die Ferne wird
    /// weiter klar. Im Himmel nur der erste Durchlauf: Dort fand die Schätzung sonst immer wieder „Dunst“
    /// (Rauschen, Verlauf), der Himmel wurde körnig und fleckig.</param>
    /// <param name="final">Letzter Durchlauf: Klarheit, Schärfe, Kurve und Dynamik anwenden (einmal, nicht je Durchlauf).</param>
    /// <param name="share">Anteil der Wahrnehmung (Klarheit, Schärfe, Kurve, Dynamik) in diesem Durchlauf;
    /// die Schleife nimmt 1 in jeder Runde — wie das Auge: Kontrast und Farbe nach jeder Runde, die nächste
    /// schätzt den kontrastreicheren Stand neu. Nur am Schluss angewandt, brachten die Durchläufe fast nichts.</param>
    internal static byte[] Apply(byte[] bgra, int width, int height, float amount, Pass? pass, Settings s, float share)
    {
        if (amount == 0) return (byte[])bgra.Clone();
        var strength = Math.Abs(amount);
        // Regler −100 … +300: bis 100 wie das physikalische Modell (Mischung Original → Ergebnis),
        // darüber wird die geschätzte Dunstmenge stufenlos bis aufs Dreifache verstärkt — das Modell
        // allein wirkte bei Gegenlicht und leichtem Dunst sehr dezent.
        var mix = Math.Min(1, strength);
        var boost = amount > 0 ? Math.Max(1, strength) : 1;

        // Alle Schätzungen auf einer verkleinerten Fassung (Flächenmittel in linearem Licht);
        // das große Bild wird danach nur einmal durchlaufen — Panoramen haben 80 MP, eine
        // Kopie in Fließkomma bräuchte Gigabytes.
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)WorkEdge));
        var (small, sw, sh) = DownscaleLinear(bgra, width, height, f);
        var fresh = pass is null || pass.Air.Length == 0;
        var sky = fresh ? SkyMap(small, sw, sh) : pass!.Sky;
        var air = fresh ? AirLightField(small, sw, sh, sky) : pass!.Air;   // B, G, R je Pixel der kleinen Fassung
        if (pass is not null && fresh) { pass.Air = air; pass.Sky = sky; pass.W = sw; pass.H = sh; }
        var t = Transmission(small, sw, sh, air, 0.95f);
        if (!fresh)
            for (var p = 0; p < t.Length; p++) t[p] += (1 - t[p]) * Math.Min(1, sky[p]);   // Himmel: nur im ersten Durchlauf
        var boosted = ProtectSky(sky, t, boost);
        if (boosted != null) LimitBoost(small, sw, sh, air, t, boosted);
        var guide = Encode(small);
        var refine = Math.Max(1.0, RefinePerEdge * Math.Max(sw, sh));
        var plain = GuidedColor.Coefficients(guide, t, sw, sh, refine, eps: 1e-3f);
        var strong = boosted == null ? default : GuidedColor.Coefficients(guide, boosted, sw, sh, refine, eps: 1e-3f);

        // Luftlicht an einer Stelle des großen Bildes: bilinear aus dem Feld der kleinen Fassung
        void AirAt(float sx, float sy, float[] into)
        {
            for (var c = 0; c < 3; c++) into[c] = At(air, sw, sh, sx, sy, c);
        }

        // Durchlässigkeit an einer Stelle (Koordinaten der kleinen Fassung), Führungsbild gamma-kodiert
        float TransmitAt(float sx, float sy, float gB, float gG, float gR, float b, float g, float r, float[] a)
        {
            var skyAt = At(sky, sw, sh, sx, sy, 0, 1);
            var landHere = 1 - Math.Clamp(skyAt, 0, 1);   // über 1 = Lichthof, zählt als Himmel
            var haloHere = Math.Clamp(skyAt - 1, 0, 1);
            var tt = Tolerate(Math.Clamp(Sample(plain, sw, sh, sx, sy, gB, gG, gR), 0, 1), b, g, r, a, 0, landHere);
            if (boosted != null)
                tt = Math.Min(tt, Tolerate(Math.Clamp(Sample(strong, sw, sh, sx, sy, gB, gG, gR), 0, 1), b, g, r, a, 0, landHere));
            // Lichthof der Sonne: Licht, kein Dunst — nichts zurückrechnen. 0.4.38 erreichte das über die
            // Regel „heller als das Luftlicht“; ohne sie hellte die Rückrechnung den Hof auf, die
            // Strahlen liefen ins Weiß (Schwankung bei r 120: ±8,5 statt ±11,3 im Labor).
            return tt + (1 - tt) * haloHere;
        }

        // Helligkeitsausgleich: Verhältnis Original/Ergebnis auf der kleinen Fassung
        (float[] A, float[] B) gain = default;
        if (amount > 0)
        {
            var after = new float[small.Length];
            var a3 = new float[3];
            for (var p = 0; p < sw * sh; p++)
            {
                var o = p * 3;
                a3[0] = air[o]; a3[1] = air[o + 1]; a3[2] = air[o + 2];
                var tt = TransmitAt(p % sw, p / sw, guide[p * 3], guide[p * 3 + 1], guide[p * 3 + 2],
                    small[p * 3], small[p * 3 + 1], small[p * 3 + 2], a3);
                for (var c = 0; c < 3; c++) after[p * 3 + c] = Recover(small[p * 3 + c], air[o + c], tt, mix);
            }
            gain = BrightnessGain(small, after, guide, sw, sh);
        }
        var k = s.BrightnessMatch * mix;
        // Buntheit: Dunst nimmt sie um den Faktor t — bis 1/t zurück ist berechtigt, dazu ein
        // kleiner Rest; dünner Dunst (Felder in mittlerer Ferne) bekommt so kaum mehr Farbe

        // Wahrnehmung wie Referenzwerkzeug: Das Modell allein lässt Smog und dünnen Dunst fast
        // unverändert (der dunkle Kanal findet zwischen Häusern und Bäumen immer Schwarzes) —
        // dazu lokaler Kontrast (Klarheit), feine Schärfe, sanfte S-Kurve und Dynamik, mit dem
        // Regler wachsend, über 100 langsamer. Klarheit und Schärfe nicht im Himmel (Rauschen).
        var over = Math.Max(0, strength - 1);
        var perceptual = share;
        var clarity = perceptual * (s.Clarity * mix + s.Clarity * 0.4f * over);
        var sharpen = perceptual * (s.Sharpen * mix + s.Sharpen * 0.4f * over);
        var curve = perceptual * (s.Curve * mix + s.Curve * 0.5f * over);
        var vibrance = perceptual * (s.Vibrance * mix + s.Vibrance * 0.4f * over);
        (float[] A, float[] B) baseL = default;
        if (amount > 0)
        {
            // Grundhelligkeit für die Klarheit: Oklab-L des Originals, kantentreu geglättet —
            // ein einfacher Weichzeichner gäbe Lichthöfe an Horizont und Häuserkanten
            var l = new float[sw * sh];
            for (var p = 0; p < l.Length; p++) l[p] = Oklab.FromLinear(small[p * 3 + 2], small[p * 3 + 1], small[p * 3]).L;
            baseL = GuidedColor.Coefficients(guide, l, sw, sh, Math.Max(1.0, ClarityPerEdge * Math.Max(sw, sh)), eps: 1e-3f);
        }

        var result = new byte[bgra.Length];
        Parallel.For(0, height, y =>
        {
            var sy = (y + 0.5f) / f - 0.5f;
            Span<float> lin = stackalloc float[3];
            var airHere = new float[3];
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var sx = (x + 0.5f) / f - 0.5f;
                var i = p * 4;
                AirAt(sx, sy, airHere);
                float gB = bgra[i] / 255f, gG = bgra[i + 1] / 255f, gR = bgra[i + 2] / 255f;   // Führungsbild = Original
                for (var c = 0; c < 3; c++) lin[c] = Srgb.ToLinear(bgra[i + c]);

                var tt = TransmitAt(sx, sy, gB, gG, gR, lin[0], lin[1], lin[2], airHere);
                var skyHere = At(sky, sw, sh, sx, sy, 0, 1);
                var land = 1 - Math.Clamp(skyHere, 0, 1);
                var halo = Math.Clamp(skyHere - 1, 0, 1);   // Lichthof der Sonne (Himmel über 1)

                float oB, oG, oR;
                if (amount > 0)
                {
                    oB = Recover(lin[0], airHere[0], tt, mix);
                    oG = Recover(lin[1], airHere[1], tt, mix);
                    oR = Recover(lin[2], airHere[2], tt, mix);

                    // Helligkeit großräumig ans Original — aber nie heller als das Original
                    // (sonst heller Streifen über dem Horizont)
                    var g = MathF.Exp(Math.Clamp(Sample(gain, sw, sh, sx, sy, gB, gG, gR), -0.7f, 1.1f) * k);
                    if (g > 1)
                    {
                        var before = Luma(lin[0], lin[1], lin[2]);
                        g = Math.Min(g, Math.Max(1, before / Math.Max(Luma(oB, oG, oR), 1e-4f)));
                    }
                    // Nur in der Landschaft: Das lineare Modell des Ausgleichs extrapoliert auf die Sonne (heller
                    // als jeder Dunst) — Zentrum dunkler, Hof heller, die Strahlen verschwammen.
                    g = 1 + (g - 1) * land;
                    oB *= g; oG *= g; oR *= g;

                    // Farbe: Helligkeit aus dem Modell, Farbton aus dem Original, Buntheit
                    // begrenzt (Oklab). Das Modell rechnet den dunkelsten Kanal gegen null —
                    // bei Grün das Blau, Felder wurden ocker und braun.
                    var was = Oklab.FromLinear(lin[2], lin[1], lin[0]);
                    var now = Oklab.FromLinear(Math.Max(0, oR), Math.Max(0, oG), Math.Max(0, oB));
                    var c0 = MathF.Sqrt(was.A * was.A + was.B * was.B);
                    var c1 = MathF.Sqrt(now.A * now.A + now.B * now.B);
                    // Im Himmel keine zusätzliche Buntheit: Die Rückrechnung machte den Himmel blauer,
                    // der Lichthof der Sonne (dunstfarben, bleibt stehen) wirkte dann als pinker Ring.
                    var chromaCap = 1 + land * (mix * (1 / tt - 1) + strength * ChromaPerUnit);   // tt ist über 100 schon verstärkt
                    var ratio = c0 > 1e-3f ? Math.Min(c1 / c0, chromaCap) : 1;

                    // Klarheit, Schärfe, Kurve (auf der Helligkeit), Dynamik (auf der Buntheit)
                    var L = now.L
                          + clarity * land * (was.L - Sample(baseL, sw, sh, sx, sy, gB, gG, gR))
                          + sharpen * land * FineDetail(bgra, width, height, x, y);
                    L = Math.Clamp(L, 0, 1);
                    // Kurve nicht im Lichthof: Ihre Schulter staucht die Strahlen (gemessen −11 % Schwankung)
                    L -= curve * (1 - halo) * MathF.Sin(2 * MathF.PI * L) / (2 * MathF.PI);
                    var chroma = c0 * ratio;
                    ratio *= 1 + vibrance * land * (1 - Math.Min(1, chroma / VibranceFull));   // nicht im Himmel, auch nicht im Lichthof

                    var (sR, sG, sB) = Oklab.ToSrgb(L, was.A * ratio, was.B * ratio);
                    result[i] = (byte)(sB * 255 + 0.5f);
                    result[i + 1] = (byte)(sG * 255 + 0.5f);
                    result[i + 2] = (byte)(sR * 255 + 0.5f);
                    result[i + 3] = bgra[i + 3];
                    continue;
                }
                else
                {
                    // Dunst hinzufügen: Grundschleier plus mehr, wo es schon dunstig/fern ist
                    var add = strength * (0.3f + 0.5f * (1 - tt));
                    oB = lin[0] + (airHere[0] - lin[0]) * add;
                    oG = lin[1] + (airHere[1] - lin[1]) * add;
                    oR = lin[2] + (airHere[2] - lin[2]) * add;
                }

                result[i] = Srgb.ToByte(oB);
                result[i + 1] = Srgb.ToByte(oG);
                result[i + 2] = Srgb.ToByte(oR);
                result[i + 3] = bgra[i + 3];
            }
        });
        return result;
    }

    /// <summary>
    /// Feine Struktur an einem Pixel: Helligkeit (gamma-kodiert) minus 3×3-Binomialmittel —
    /// das, was eine leichte Unscharfmaskierung verstärkt.
    /// </summary>
    private static float FineDetail(byte[] bgra, int w, int h, int x, int y)
    {
        float Y(int xx, int yy)
        {
            var i = (Math.Clamp(yy, 0, h - 1) * w + Math.Clamp(xx, 0, w - 1)) * 4;
            return (0.0722f * bgra[i] + 0.7152f * bgra[i + 1] + 0.2126f * bgra[i + 2]) / 255f;
        }
        var blur = (4 * Y(x, y)
                    + 2 * (Y(x - 1, y) + Y(x + 1, y) + Y(x, y - 1) + Y(x, y + 1))
                    + Y(x - 1, y - 1) + Y(x + 1, y - 1) + Y(x - 1, y + 1) + Y(x + 1, y + 1)) / 16;
        return Y(x, y) - blur;
    }

    /// <summary>Szene zurückrechnen, mit der Stärke zwischen Original und voller Wirkung.</summary>
    private static float Recover(float v, float air, float t, float strength) =>
        v + ((v - air) / t + air - v) * strength;

    /// <summary>Lineares Modell des Guided Filters an einer Stelle: aᵀ·I + b (bilinear aus der kleinen Fassung).</summary>
    private static float Sample((float[] A, float[] B) m, int sw, int sh, float x, float y, float gB, float gG, float gR) =>
        At(m.A, sw, sh, x, y, 0) * gB + At(m.A, sw, sh, x, y, 1) * gG + At(m.A, sw, sh, x, y, 2) * gR + At(m.B, sw, sh, x, y, 0, 1);

    /// <summary>
    /// Toleranz: Was dem Luftlicht sehr ähnlich ist (Himmel am Horizont, weiße Wände), gilt
    /// nicht als dichter Dunst — sonst Farbstiche und Rauschen. Dazu die Grenzen je Kanal.
    /// </summary>
    /// <param name="air">Luftlicht (B, G, R), <paramref name="o"/> = Beginn des Tripels.</param>
    /// <param name="land">1 = Landschaft, 0 = Himmel. Im Himmel gelten nur die Grenzen, nicht die
    /// Pixelregeln: Der Lichthof der Sonne hat die Farbe des Dunstes und wurde geschont, der Himmel
    /// daneben wurde blauer — ein eingefärbter Ring um die Sonne. Im Himmel gibt es keine Kanten,
    /// die glatte Karte reicht.</param>
    private static float Tolerate(float t, float b, float g, float r, float[] air, int o, float land = 1)
    {
        float aB = air[o], aG = air[o + 1], aR = air[o + 2];
        var smooth = Math.Max(t, Math.Max(Keep(b, aB, 0), Math.Max(Keep(g, aG, 0), Keep(r, aR, 0))));
        if (land <= 0) return Math.Clamp(smooth, MinTransmission, 1);

        var dist = Math.Max(Math.Abs(b - aB), Math.Max(Math.Abs(g - aG), Math.Abs(r - aR)));
        if (dist < Tolerance) t = Math.Min(1, t * Tolerance / Math.Max(dist, 1e-4f));
        t = Math.Max(t, Math.Max(Keep(b, aB, 0), Math.Max(Keep(g, aG, 0), Keep(r, aR, 0))));

        // Deutlich heller als das Luftlicht dieser Richtung — kanalweise: eine Lichtquelle
        // (Sonne, ihr Lichthof, Lampe), kein Dunst. Erst ab 5 % darüber: Mit einem Luftlicht für
        // das ganze Bild und der Grenze knapp darunter galt der helle Dunst unter der Sonne als
        // Lichtquelle — genau der dichteste Dunst blieb stehen.
        var over = Math.Max(b / aB, Math.Max(g / aG, r / aR));
        t += (1 - t) * SmoothStep(1.05f, 1.2f, over);
        // Fast weiß (an der Grenze der Aufnahme): Sonne, Lichter, Spitzlichter — nie Dunst
        t += (1 - t) * SmoothStep(0.85f, 0.97f, Luma(b, g, r));
        return Math.Clamp(smooth + (t - smooth) * land, MinTransmission, 1);
    }

    /// <summary>Anteil, den jeder Farbkanal bei verstärktem Regler (über 100) mindestens behält.</summary>
    private const float KeepShare = 0.3f;

    /// <summary>
    /// Kleinste Durchlässigkeit, bei der ein Kanal im erlaubten Bereich bleibt: unter dem
    /// Luftlicht behält er mindestens <paramref name="share"/> seines Werts (0: nicht unter
    /// Schwarz), darüber läuft er nicht über Weiß. Ohne diese Grenze rechnete ein kräftiger
    /// Regler über das Ziel hinaus — Kanäle wurden abgeschnitten, rote und schwarze Bänder am
    /// Horizont, Farbsäume an der Sonne.
    /// </summary>
    private static float Keep(float v, float air, float share) => v < air
        ? (air - v) / Math.Max(air - share * v, 1e-4f)
        : air < 1 ? (v - air) / (1 - air) : 0;

    /// <summary>
    /// Regler über 100: Die Verstärkung nähert sich weich der Grenze, bei der im Mittel der
    /// Umgebung (Fenster des dunklen Kanals) jeder Kanal noch <see cref="KeepShare"/> behält —
    /// nie unter die Wirkung bei 100.
    /// </summary>
    /// <remarks>
    /// Eine Grenze je Pixel hielt die dunklen Stellen fest, während die helleren daneben weiter
    /// abgesenkt wurden — der Kontrast nahm wieder ab. Das Maximum der Umgebung liegt nie unter
    /// der Wirkung bei 100 (der dunkle Kanal rechnet das dunkelste Pixel dort schon auf ~5 %) —
    /// der Regler wirkte über 100 kaum. Eine harte Grenze ließ ihn ab etwa 150 stillstehen.
    /// </remarks>
    private static void LimitBoost(float[] lin, int w, int h, float[] air, float[] t, float[] boosted)
    {
        var floor = new float[w * h];
        for (var p = 0; p < floor.Length; p++)
        {
            var o = p * 3;
            floor[p] = Math.Max(Keep(lin[p * 3], air[o], KeepShare),
                Math.Max(Keep(lin[p * 3 + 1], air[o + 1], KeepShare), Keep(lin[p * 3 + 2], air[o + 2], KeepShare)));
        }
        Blur.Gauss(floor, w, h, Math.Max(1.0, PatchPerEdge * Math.Max(w, h)));
        for (var p = 0; p < floor.Length; p++)
        {
            // Weich an die Grenze: anfangs voll, dann immer langsamer — keine Stufe, kein Stillstand
            var room = t[p] - floor[p];
            boosted[p] = room <= 0 ? t[p] : t[p] - room * (1 - MathF.Exp(-Math.Max(0, t[p] - boosted[p]) / room));
        }
    }

    /// <summary>
    /// Durchlässigkeit t = 1 − ω · dunkler Kanal des aufs Luftlicht normierten Bildes.
    /// </summary>
    /// <param name="air">Luftlicht je Spalte (B, G, R).</param>
    internal static float[] Transmission(float[] lin, int w, int h, float[] air, float omega)
    {
        var n = w * h;
        var minRgb = new float[n];
        for (var p = 0; p < n; p++)
        {
            var o = p * 3;
            minRgb[p] = Math.Min(lin[p * 3] / air[o], Math.Min(lin[p * 3 + 1] / air[o + 1], lin[p * 3 + 2] / air[o + 2]));
        }
        var dark = MinFilter(minRgb, w, h, Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(w, h))));
        var t = new float[n];
        for (var p = 0; p < n; p++) t[p] = 1 - omega * dark[p];
        return t;
    }

    /// <summary>
    /// Luftlicht (B, G, R, linear) für das ganze Bild: unter den Pixeln, deren dunkler Kanal zu
    /// den hellsten 1 % gehört, die mittleren 20 % gemittelt (Adobe: Median). Die Sonne oder
    /// Lichter bestimmen es so nicht. Mindestens 0,25 (linear), damit dunkle Bilder ohne Dunst
    /// nicht kaputtgerechnet werden.
    /// </summary>
    internal static float[] AirLight(float[] lin, int w, int h) =>
        AirLightOf(lin, DarkChannel(lin, w, h), Enumerable.Range(0, w * h).ToArray());

    /// <summary>Senkrechte Streifen für das Luftlicht je Richtung.</summary>
    private const int AirStrips = 8;

    /// <summary>
    /// Luftlicht je Spalte (B, G, R je Spalte): wie <see cref="AirLight"/>, aber für jeden von
    /// <see cref="AirStrips"/> senkrechten Streifen, zwischen den Streifenmitten gemittelt. Die
    /// Farbe des Dunstes hängt von der Richtung zur Sonne ab — zur Sonne hin hell und warm, von
    /// ihr weg blau und dunkler. Gemessen am Horizont eines Panoramas: 0,33 … 0,97 (linear); ein
    /// einziger Wert aus der Gegend der Sonne unterschätzte den Dunst abseits davon auf die Hälfte.
    /// </summary>
    /// <remarks>
    /// Streifen ohne Himmel und Dunst (nur Bäume: dunkler Kanal 0,01) taugen nicht — dort gilt
    /// der Wert für das ganze Bild, mit weichem Übergang. Jeder Kanal bleibt zwischen 30 % und
    /// 125 % des Gesamtwerts.
    /// </remarks>
    internal static float[] AirLightColumns(float[] lin, int w, int h, float[]? sky = null)
    {
        var dark = DarkChannel(lin, w, h);
        // Nur aus der Landschaft, wo sie im Dunst verschwindet — nicht aus dem Himmel: Der ist
        // oft heller als der Dunst vor der Landschaft (überbelichtet, Glühen um die Sonne), und an
        // „Weiß“ gemessen wirkte dichter Smog über einer Stadt dünn (erkannt 1–12 %).
        int[] Landscape(int[] pixels)
        {
            if (sky is null) return pixels;
            var land = pixels.Where(p => sky[p] < 0.5f).ToArray();
            return land.Length >= pixels.Length / 20 ? land : pixels;
        }

        var global = AirLightOf(lin, dark, Landscape(Enumerable.Range(0, w * h).ToArray()));
        var globalDark = Math.Min(global[0], Math.Min(global[1], global[2]));

        var strips = new float[AirStrips][];
        for (var k = 0; k < AirStrips; k++)
        {
            int x0 = k * w / AirStrips, x1 = Math.Max(x0 + 1, (k + 1) * w / AirStrips);
            var pixels = new int[(x1 - x0) * h];
            for (int y = 0, i = 0; y < h; y++)
                for (var x = x0; x < x1; x++) pixels[i++] = y * w + x;
            pixels = Landscape(pixels);
            var a = AirLightOf(lin, dark, pixels);

            // Wie verlässlich: dunkler Kanal der Kandidaten im Vergleich zum ganzen Bild
            var candidates = pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).Average(p => dark[p]);
            var trust = SmoothStep(0.1f, 0.25f, (float)candidates / globalDark);
            for (var c = 0; c < 3; c++)
                a[c] = global[c] + (Math.Clamp(a[c], 0.3f * global[c], 1.25f * global[c]) - global[c]) * trust;
            strips[k] = a;
        }

        var air = new float[w * 3];
        for (var x = 0; x < w; x++)
        {
            var pos = Math.Clamp((x + 0.5f) / w * AirStrips - 0.5f, 0, AirStrips - 1);
            int k0 = (int)pos, k1 = Math.Min(k0 + 1, AirStrips - 1);
            for (var c = 0; c < 3; c++)
                air[x * 3 + c] = strips[k0][c] + (strips[k1][c] - strips[k0][c]) * (pos - k0);
        }
        return air;
    }


    /// <summary>Kacheln für das Luftlichtfeld (Spalten × Zeilen).</summary>
    private const int AirCols = 6, AirRows = 4;

    /// <summary>
    /// Luftlicht als Feld (B, G, R je Pixel der kleinen Fassung): wie <see cref="AirLightColumns"/>,
    /// aber je Kachel statt je Streifen, zwischen den Kachelmitten bilinear. Die Farbe des Schleiers
    /// kippt in verschiedene Richtungen — um die Sonne gelbweiß, abseits bläulich, im Tal grau. Ein
    /// Wert je Spalte zog überall dieselbe Farbe ab: Ring um die Sonne, rosa Ferne.
    /// </summary>
    internal static float[] AirLightField(float[] lin, int w, int h, float[]? sky = null)
    {
        var dark = DarkChannel(lin, w, h);
        int[] Landscape(int[] pixels)
        {
            if (sky is null) return pixels;
            var land = pixels.Where(p => sky[p] < 0.5f).ToArray();
            return land.Length >= pixels.Length / 20 ? land : pixels;
        }
        // Senkrecht nur dort abweichen, wo die Kachel so dichten Dunst enthält wie ihre Spalte am
        // Horizont: Das Luftlicht ist die Farbe in unendlicher Ferne — eine Kachel im Tal sieht vor
        // allem Szenenfarbe, ihr „Luftlicht“ wäre zu dunkel (gemessen: halbe Farbrückgewinnung).
        var columns = AirLightColumns(lin, w, h, sky);
        var columnDark = new float[AirCols];
        for (var tx = 0; tx < AirCols; tx++)
        {
            int x0 = tx * w / AirCols, x1 = Math.Max(x0 + 1, (tx + 1) * w / AirCols);
            var pixels = new int[(x1 - x0) * h];
            for (int y = 0, i = 0; y < h; y++)
                for (var x = x0; x < x1; x++) pixels[i++] = y * w + x;
            pixels = Landscape(pixels);
            columnDark[tx] = (float)pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).Average(p => dark[p]);
        }

        var tiles = new float[AirCols * AirRows][];
        for (var ty = 0; ty < AirRows; ty++)
        for (var tx = 0; tx < AirCols; tx++)
        {
            int x0 = tx * w / AirCols, x1 = Math.Max(x0 + 1, (tx + 1) * w / AirCols);
            int y0 = ty * h / AirRows, y1 = Math.Max(y0 + 1, (ty + 1) * h / AirRows);
            var pixels = new int[(x1 - x0) * (y1 - y0)];
            for (int y = y0, i = 0; y < y1; y++)
                for (var x = x0; x < x1; x++) pixels[i++] = y * w + x;
            pixels = Landscape(pixels);
            var a = AirLightOf(lin, dark, pixels);
            var candidates = pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).Average(p => dark[p]);
            var trust = SmoothStep(0.7f, 0.95f, (float)candidates / Math.Max(1e-3f, columnDark[tx]));
            var cx = Math.Clamp((x0 + x1) / 2, 0, w - 1) * 3;
            for (var c = 0; c < 3; c++)
            {
                var col = columns[cx + c];
                a[c] = col + (Math.Clamp(a[c], 0.5f * col, 1.25f * col) - col) * trust;
            }
            tiles[ty * AirCols + tx] = a;
        }

        var air = new float[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            var py = Math.Clamp((y + 0.5f) / h * AirRows - 0.5f, 0, AirRows - 1);
            int r0 = (int)py, r1 = Math.Min(r0 + 1, AirRows - 1); var fy = py - r0;
            for (var x = 0; x < w; x++)
            {
                var px = Math.Clamp((x + 0.5f) / w * AirCols - 0.5f, 0, AirCols - 1);
                int c0 = (int)px, c1 = Math.Min(c0 + 1, AirCols - 1); var fx = px - c0;
                for (var c = 0; c < 3; c++)
                {
                    var top = tiles[r0 * AirCols + c0][c] * (1 - fx) + tiles[r0 * AirCols + c1][c] * fx;
                    var bottom = tiles[r1 * AirCols + c0][c] * (1 - fx) + tiles[r1 * AirCols + c1][c] * fx;
                    air[(y * w + x) * 3 + c] = top * (1 - fy) + bottom * fy;
                }
            }
        }
        return air;
    }

    /// <summary>Dunkler Kanal (kleinster Farbkanal, Minimum über das Fenster), nicht normiert.</summary>
    private static float[] DarkChannel(float[] lin, int w, int h)
    {
        var minRgb = new float[w * h];
        for (var p = 0; p < minRgb.Length; p++) minRgb[p] = Math.Min(lin[p * 3], Math.Min(lin[p * 3 + 1], lin[p * 3 + 2]));
        return MinFilter(minRgb, w, h, Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(w, h))));
    }

    /// <summary>Luftlicht aus einer Auswahl von Pixeln: hellste 1 % des dunklen Kanals, davon die mittleren 20 %.</summary>
    private static float[] AirLightOf(float[] lin, float[] dark, int[] pixels)
    {
        var top = pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).ToArray();
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
    /// <returns>Die verstärkte Durchlässigkeit für den Regler über 100, sonst null.</returns>
    private static float[]? ProtectSky(float[] sky, float[] t, float boost)
    {
        var boosted = boost > 1 ? new float[t.Length] : null;
        for (var i = 0; i < t.Length; i++)
        {
            // Regler über 100: Dunstmenge verstärken — nur in der Landschaft. Die Verstärkung klingt
            // zum Himmel hin viel schneller ab als der Schutz greift; sonst bildeten sich bei 200–300
            // im Übergang Himmel/Horizont Bänder (blau, weiß-rosa) und ein dunkler Bogen.
            if (boosted != null)
            {
                var skyHere = Math.Min(1, sky[i]);   // über 1 = Lichthof, zählt als Himmel
                var landscape = (1 - skyHere) * (1 - skyHere) * (1 - skyHere) * (1 - skyHere);
                var tb = Math.Max(0, 1 - (1 - t[i]) * (1 + (boost - 1) * landscape));
                boosted[i] = tb + (1 - tb) * SkyProtection * skyHere;
            }
            t[i] += (1 - t[i]) * SkyProtection * Math.Min(1, sky[i]);
        }
        return boosted;
    }

    /// <summary>Himmel je Pixel (0 … 1): hell und ohne Struktur — siehe <see cref="ProtectSky"/>.</summary>
    internal static float[] SkyMap(float[] lin, int w, int h)
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

        // Lichtquelle mit Hof: Was um ausgefressene Pixel (Sonne, Lampe) herum liegt, ist Licht,
        // kein Dunst — die Strahlen gelten sonst als Struktur, der Hof als Landschaft: Die
        // Pixelregeln verdoppelten dort die Buntheit (gemessen 0,023 → 0,045), ein pinker Ring.
        var hot = new float[l.Length];
        for (var i = 0; i < hot.Length; i++) hot[i] = l[i] > HotLuma ? 1 : 0;
        Blur.Gauss(hot, w, h, Math.Max(2.0, HaloPerEdge * edge));
        // Auf den Scheitel normiert: Der Hof reicht so bei jeder Bildgröße gleich weit (bis in die
        // Strahlen); unnormiert war er bei kleinen Bildern fast leer und wurde nach außen zu schwach —
        // die Rückrechnung hellte die Strahlen auf. Ein einzelnes heißes Pixel zählt nicht (Gate).
        var peak = hot.Max();
        var haloScale = peak > 0.001f ? 1 / peak : 0;   // Gate: eine ausgefressene Scheibe, kein einzelnes Pixel

        var sky = new float[l.Length];
        for (var i = 0; i < sky.Length; i++)
        {
            var structure = band[i] / Math.Max(mean[i], 1e-3f);
            var smooth = 1 - SmoothStep(0.008f, 0.02f, structure);
            var bright = SmoothStep(0.03f, 0.1f, l[i]);   // dunkle glatte Schatten sind kein Himmel
            // Hof: nahe der Sonne und hell — die Grenze folgt der Helligkeit des Himmels, nicht einem
            // Radius; ein reiner Radius ergab eine sichtbar hellere Scheibe um die Sonne (innen
            // unverändert, außen entdunstet). 0.4.38 schonte ebenfalls pixelweise (heller als Luftlicht).
            var halo = SmoothStep(0.02f, 0.3f, hot[i] * haloScale) * SmoothStep(0.45f, 0.75f, l[i]);
            // Werte über 1 markieren den Lichthof (Himmel = 1 + Hof): Die feine Schärfe bleibt dort
            // voll — die Strahlen der Sonne sind Struktur, die man sehen will (in der App waren sie
            // weich, im Labor mit normaler Schärfe kräftig); nur dunstiger Himmel wird geschont.
            sky[i] = Math.Max(bright * smooth, halo) + halo;
        }
        return sky;
    }

    /// <summary>Ab dieser Helligkeit (linear) ist ein Pixel Lichtquelle (ausgefressen).</summary>
    private const float HotLuma = 0.95f;

    /// <summary>Reichweite des Lichthofs um eine Lichtquelle (Gauß-Sigma), relativ zur längsten Kante.</summary>
    private const double HaloPerEdge = 0.05;

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
