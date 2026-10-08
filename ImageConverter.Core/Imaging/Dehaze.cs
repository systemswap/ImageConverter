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

    /// <summary>
    /// Senkrecht nur ein Viertel davon: In Landschaften ändert sich der Dunst mit der Entfernung,
    /// also von Zeile zu Zeile. Ein quadratisches Fenster griff nach unten in Zeilen mit weniger
    /// Dunst und nahm deren dunklere Werte — die Dichte wurde unter dem Horizont um 0,1
    /// unterschätzt (gemessen am künstlichen Dunst mit bekannter Dichte).
    /// </summary>
    private const double PatchPerEdgeY = 0.003;

    private static int PatchRadius(int w, int h) => Math.Max(1, (int)Math.Round(PatchPerEdge * Math.Max(w, h)));
    private static int PatchRadiusY(int w, int h) => Math.Max(1, (int)Math.Round(PatchPerEdgeY * Math.Max(w, h)));

    /// <summary>Guided Filter für die Durchlässigkeit (Radius als Gauß-Sigma), relativ.</summary>
    private const double RefinePerEdge = 0.025;

    /// <summary>Helligkeitsausgleich: Glättung der Helligkeit (großräumig), relativ.</summary>
    private const double BrightnessPerEdge = 0.05;

    /// <summary>Anteil des Helligkeitsausgleichs (1 = voll ans Original).</summary>
    private const float BrightnessMatch = 0.5f;

    /// <summary>Toleranz (lineares Licht): Farben näher am Luftlicht gelten nicht als dichter Dunst.</summary>
    private const float Tolerance = 0.08f;

    /// <summary>
    /// Buntheit: höchstens so viel mehr als im Original, je Einheit Regler, zusätzlich zu dem,
    /// was der Dunst genommen hat (1/t). Das Modell selbst gäbe bei dünnem Dunst viel mehr —
    /// es rechnet den dunkelsten Kanal gegen null, bei Grün ist das Blau: Felder wurden ocker
    /// und braun.
    /// </summary>
    private const float ChromaPerUnit = 0.15f;

    /// <summary>Klarheit (lokaler Kontrast, großer Radius) bei Regler 100.</summary>
    private const float ClarityPerUnit = 0.35f;

    /// <summary>Radius der Klarheit (Gauß-Sigma des Guided Filters), relativ zur längsten Kante.</summary>
    private const double ClarityPerEdge = 0.02;

    /// <summary>Feine Schärfe (3×3-Unscharfmaskierung) bei Regler 100.</summary>
    private const float SharpenPerUnit = 0.6f;

    /// <summary>S-Kurve auf der Helligkeit bei Regler 100 (Steigung in der Mitte 1 + Wert).</summary>
    private const float CurvePerUnit = 0.12f;

    /// <summary>Dynamik bei Regler 100: blasse Farben bis zu so viel mehr Buntheit.</summary>
    private const float VibrancePerUnit = 0.25f;

    /// <summary>Ab dieser Oklab-Buntheit wirkt die Dynamik nicht mehr (kräftige Farben bleiben).</summary>
    private const float VibranceFull = 0.12f;

    /// <summary>Kleinste Durchlässigkeit: begrenzt die Verstärkung.</summary>
    private const float MinTransmission = 0.1f;

    /// <param name="amount">−1 … +3: rechts Dunst entfernen (über 1 verstärkt), links Dunst hinzufügen.</param>
    public static byte[] Apply(byte[] bgra, int width, int height, float amount)
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
        var sky = SkyMap(small, sw, sh);
        var air = AirLightColumns(small, sw, sh, sky);   // je Spalte der kleinen Fassung
        sky = SkyMap(small, sw, sh, air);   // endgültig, mit dem Luftlicht
        var t =Transmission(small, sw, sh, air, 0.95f);
        var boosted = ProtectSky(sky, t, boost);
        if (boosted != null) LimitBoost(small, sw, sh, air, t, boosted);
        var guide = Encode(small);
        var refine = Math.Max(1.0, RefinePerEdge * Math.Max(sw, sh));
        var plain = GuidedColor.Coefficients(guide, t, sw, sh, refine, eps: 1e-3f);
        var strong = boosted == null ? default : GuidedColor.Coefficients(guide, boosted, sw, sh, refine, eps: 1e-3f);

        // Luftlicht je Spalte des großen Bildes (zwischen den Spalten der kleinen Fassung gemittelt)
        var airFull = new float[width * 3];
        for (var x = 0; x < width; x++)
        {
            var sx = Math.Clamp((x + 0.5f) / f - 0.5f, 0, sw - 1);
            int x0 = (int)sx, x1 = Math.Min(x0 + 1, sw - 1);
            for (var c = 0; c < 3; c++)
                airFull[x * 3 + c] = air[x0 * 3 + c] + (air[x1 * 3 + c] - air[x0 * 3 + c]) * (sx - x0);
        }

        // Durchlässigkeit an einer Stelle (Koordinaten der kleinen Fassung), Führungsbild gamma-kodiert
        float TransmitAt(float sx, float sy, float gB, float gG, float gR, float b, float g, float r, float[] a, int o)
        {
            var tt = Tolerate(Math.Clamp(Sample(plain, sw, sh, sx, sy, gB, gG, gR), 0, 1), b, g, r, a, o);
            if (boosted == null) return tt;
            var strongT = Tolerate(Math.Clamp(Sample(strong, sw, sh, sx, sy, gB, gG, gR), 0, 1), b, g, r, a, o);
            return Math.Min(tt, strongT);
        }

        // Helligkeitsausgleich: Verhältnis Original/Ergebnis auf der kleinen Fassung
        (float[] A, float[] B) gain = default;
        if (amount > 0)
        {
            var after = new float[small.Length];
            for (var p = 0; p < sw * sh; p++)
            {
                var o = p % sw * 3;
                var tt = TransmitAt(p % sw, p / sw, guide[p * 3], guide[p * 3 + 1], guide[p * 3 + 2],
                    small[p * 3], small[p * 3 + 1], small[p * 3 + 2], air, o);
                for (var c = 0; c < 3; c++) after[p * 3 + c] = Recover(small[p * 3 + c], air[o + c], tt, mix);
            }
            gain = BrightnessGain(small, after, guide, sw, sh);
        }
        var k = BrightnessMatch * mix;
        // Buntheit: Dunst nimmt sie um den Faktor t — bis 1/t zurück ist berechtigt, dazu ein
        // kleiner Rest; dünner Dunst (Felder in mittlerer Ferne) bekommt so kaum mehr Farbe

        // Wahrnehmung wie Referenzwerkzeug: Das Modell allein lässt Smog und dünnen Dunst fast
        // unverändert (der dunkle Kanal findet zwischen Häusern und Bäumen immer Schwarzes) —
        // dazu lokaler Kontrast (Klarheit), feine Schärfe, sanfte S-Kurve und Dynamik, mit dem
        // Regler wachsend, über 100 langsamer. Klarheit und Schärfe nicht im Himmel (Rauschen).
        var over = Math.Max(0, strength - 1);
        var clarity = ClarityPerUnit * mix + ClarityPerUnit * 0.4f * over;
        var sharpen = SharpenPerUnit * mix + SharpenPerUnit * 0.4f * over;
        var curve = CurvePerUnit * mix + CurvePerUnit * 0.5f * over;
        var vibrance = VibrancePerUnit * mix + VibrancePerUnit * 0.4f * over;
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
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var sx = (x + 0.5f) / f - 0.5f;
                var i = p * 4;
                var o = x * 3;
                float gB = bgra[i] / 255f, gG = bgra[i + 1] / 255f, gR = bgra[i + 2] / 255f;   // Führungsbild = Original
                for (var c = 0; c < 3; c++) lin[c] = Srgb.ToLinear(bgra[i + c]);

                var tt = TransmitAt(sx, sy, gB, gG, gR, lin[0], lin[1], lin[2], airFull, o);

                float oB, oG, oR;
                if (amount > 0)
                {
                    oB = Recover(lin[0], airFull[o], tt, mix);
                    oG = Recover(lin[1], airFull[o + 1], tt, mix);
                    oR = Recover(lin[2], airFull[o + 2], tt, mix);

                    // Helligkeit großräumig ans Original — aber nie heller als das Original
                    // (sonst heller Streifen über dem Horizont)
                    var g = MathF.Exp(Math.Clamp(Sample(gain, sw, sh, sx, sy, gB, gG, gR), -0.7f, 1.1f) * k);
                    if (g > 1)
                    {
                        var before = Luma(lin[0], lin[1], lin[2]);
                        g = Math.Min(g, Math.Max(1, before / Math.Max(Luma(oB, oG, oR), 1e-4f)));
                    }
                    oB *= g; oG *= g; oR *= g;

                    // Farbe (Oklab): Helligkeit aus dem geklemmten Modell; Farbton aus der ungeklemmten
                    // Rückrechnung — ein Kanal unter null bleibt dort unter null, statt abgeschnitten zu
                    // werden (abgeschnitten wurde Grün zu Ocker); Buntheit begrenzt. Der Farbton des
                    // dunstigen Pixels taugte nicht: Er trägt schon den Stich des Dunstes (gemessen am
                    // künstlichen Dunst: Farbtonfehler blieb bei −25°, egal wie stark der Regler).
                    var was = Oklab.FromLinear(lin[2], lin[1], lin[0]);
                    var now = Oklab.FromLinear(Math.Max(0, oR), Math.Max(0, oG), Math.Max(0, oB));
                    var free = Oklab.FromLinear(oR, oG, oB);
                    var c0 = MathF.Sqrt(was.A * was.A + was.B * was.B);
                    var cu = MathF.Sqrt(free.A * free.A + free.B * free.B);
                    // Im Himmel bleibt der Farbton des Originals, und die Buntheit steigt nur durch die Dynamik:
                    // Die Rückrechnung zieht dem Lichthof der Sonne das warme Luftlicht ab — er kippte ins Magenta.
                    var landHere = 1 - Math.Clamp(At(sky, sw, sh, sx, sy, 0, 1), 0, 1);
                    var chromaCap = 1 + landHere * mix * (1 / tt - 1) + strength * ChromaPerUnit;   // tt ist über 100 schon verstärkt
                    var ratio = c0 > 1e-3f ? Math.Min(cu / c0, chromaCap) : 1;
                    // Richtung aus der Rückrechnung, Länge c0 (× ratio unten). Läuft die Rückrechnung weit
                    // unter null (Lichthof der Sonne: kaum Dunst, Luftlicht fast gleich), ist ihre Richtung
                    // Zufall — dann bleibt der Farbton des Originals (sonst Magenta um die Sonne).
                    float dirA = was.A, dirB = was.B;
                    if (cu > 1e-4f && c0 > 1e-4f)
                    {
                        var neg = -Math.Min(oB, Math.Min(oG, oR));
                        var trust = landHere * (neg <= 0 ? 1 : 1 - SmoothStep(0.3f, 1f, neg / (Luma(Math.Max(0, oB), Math.Max(0, oG), Math.Max(0, oR)) + neg)));
                        dirA = trust * free.A / cu + (1 - trust) * was.A / c0;
                        dirB = trust * free.B / cu + (1 - trust) * was.B / c0;
                        var len = MathF.Sqrt(dirA * dirA + dirB * dirB);
                        if (len > 1e-4f) { dirA *= c0 / len; dirB *= c0 / len; } else { dirA = was.A; dirB = was.B; }
                    }

                    // Klarheit, Schärfe, Kurve (auf der Helligkeit), Dynamik (auf der Buntheit)
                    var land = landHere;
                    var L = now.L
                          + clarity * land * (was.L - Sample(baseL, sw, sh, sx, sy, gB, gG, gR))
                          + sharpen * land * FineDetail(bgra, width, height, x, y);
                    L = Math.Clamp(L, 0, 1);
                    L -= curve * MathF.Sin(2 * MathF.PI * L) / (2 * MathF.PI);
                    var chroma = c0 * ratio;
                    ratio *= 1 + vibrance * (1 - Math.Min(1, chroma / VibranceFull));

                    var (sR, sG, sB) = Oklab.ToSrgb(L, dirA * ratio, dirB * ratio);
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
                    oB = lin[0] + (airFull[o] - lin[0]) * add;
                    oG = lin[1] + (airFull[o + 1] - lin[1]) * add;
                    oR = lin[2] + (airFull[o + 2] - lin[2]) * add;
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
    /// <param name="air">Luftlicht je Spalte (B, G, R), <paramref name="o"/> = Beginn der Spalte.</param>
    private static float Tolerate(float t, float b, float g, float r, float[] air, int o)
    {
        float aB = air[o], aG = air[o + 1], aR = air[o + 2];
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
        return Math.Clamp(t, MinTransmission, 1);
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
            var o = p % w * 3;
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
            var o = p % w * 3;
            minRgb[p] = Math.Min(lin[p * 3] / air[o], Math.Min(lin[p * 3 + 1] / air[o + 1], lin[p * 3 + 2] / air[o + 2]));
        }
        var dark = MinFilter(minRgb, w, h, PatchRadius(w, h), PatchRadiusY(w, h));
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

    /// <summary>Dunkler Kanal (kleinster Farbkanal, Minimum über das Fenster), nicht normiert.</summary>
    private static float[] DarkChannel(float[] lin, int w, int h)
    {
        var minRgb = new float[w * h];
        for (var p = 0; p < minRgb.Length; p++) minRgb[p] = Math.Min(lin[p * 3], Math.Min(lin[p * 3 + 1], lin[p * 3 + 2]));
        return MinFilter(minRgb, w, h, PatchRadius(w, h), PatchRadiusY(w, h));
    }

    /// <summary>Luftlicht aus einer Auswahl von Pixeln: hellste 1 % des dunklen Kanals, davon die mittleren 20 %.</summary>
    private static float[] AirLightOf(float[] lin, float[] dark, int[] pixels)
    {
        var top = pixels.OrderByDescending(p => dark[p]).Take(Math.Max(1, pixels.Length / 100)).ToArray();
        // Davon die hellsten 30 % (nach Helligkeit): Die Kandidaten liegen im dichtesten Dunst, aber
        // nicht unendlich fern — ihr Mittel ist dunkler als das Luftlicht. Gemessen am künstlichen Dunst
        // ohne Himmel: Mittel 0,51–0,60 bei wahrem 0,80–0,95. Sonne und Himmel sind schon ausgeschlossen.
        var bright = top.OrderByDescending(p => 0.0722f * lin[p * 3] + 0.7152f * lin[p * 3 + 1] + 0.2126f * lin[p * 3 + 2]).ToArray();
        var from = 0;
        var count = Math.Max(1, (int)(top.Length * 0.3));
        var air = new float[3];
        foreach (var p in bright.Skip(from).Take(count))
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
                var landscape = (1 - sky[i]) * (1 - sky[i]) * (1 - sky[i]) * (1 - sky[i]);
                var tb = Math.Max(0, 1 - (1 - t[i]) * (1 + (boost - 1) * landscape));
                boosted[i] = tb + (1 - tb) * SkyProtection * sky[i];
            }
            t[i] += (1 - t[i]) * SkyProtection * sky[i];
        }
        return boosted;
    }

    /// <summary>Himmel je Pixel (0 … 1): hell und ohne Struktur — siehe <see cref="ProtectSky"/>.</summary>
    /// <summary>
    /// Endgültiger Himmel, mit bekanntem Luftlicht: wie <see cref="SkyMap(float[], int, int)"/>, dazu
    /// voller Schutz (über 1, gegen <see cref="SkyProtection"/> gerechnet), wo der Himmel mindestens
    /// so hell ist wie das Luftlicht — der Lichthof um die Sonne. Dort ist kaum Dunst, die Rückrechnung
    /// verstärkt nur die kleine Abweichung der Farbe: violetter Hof.
    /// </summary>
    /// <remarks>
    /// Regeln über Farbe oder Helligkeit gegenüber dem Luftlicht taugen nicht als Himmelsmerkmal:
    /// Der Abendhimmel abseits der Sonne ist dunkler als das Glühen am Horizont und ähnlich warm —
    /// er verlor den Schutz und kippte ins Türkis. Nur die Lage (vom oberen Rand herab) ist verlässlich.
    /// </remarks>
    internal static float[] SkyMap(float[] lin, int w, int h, float[] air)
    {
        var sky = SmoothBright(lin, w, h, out var pass);
        var full = 1 / SkyProtection;
        var shield = new float[sky.Length];
        for (var p = 0; p < sky.Length; p++)
        {
            var o = p % w * 3;
            var lI = Luma(lin[p * 3], lin[p * 3 + 1], lin[p * 3 + 2]);
            var lA = Math.Max(1e-3f, Luma(air[o], air[o + 1], air[o + 2]));
            // Mindestens so hell wie das Luftlicht: Sonne, ihre Strahlen, Lichthof, helle Wolken —
            // strukturiert, aber nie dunstbedeckte Landschaft (die ist höchstens so hell wie das
            // Luftlicht, abzüglich der Durchlässigkeit). Sonst brach die Suche vom oberen Rand an der
            // Sonne ab, und alles darunter bis zum Horizont galt als Landschaft: breiter violetter
            // Streifen unter der Sonne. Ein absoluter Wert taugte nicht — heller dichter Dunst zählte mit.
            shield[p] = SmoothStep(0.95f, 1.1f, lI / lA);
            pass[p] = Math.Max(pass[p], shield[p]);
        }
        ReachFromTop(pass, sky, w, h, lin);
        for (var p = 0; p < sky.Length; p++) sky[p] *= 1 + (full - 1) * shield[p];
        return sky;
    }

    internal static float[] SkyMap(float[] lin, int w, int h)
    {
        var sky = SmoothBright(lin, w, h, out var pass);
        ReachFromTop(pass, sky, w, h, lin);
        return sky;
    }

    /// <summary>Glatt und nicht dunkel, je Pixel 0 … 1 — der Rohstoff für <see cref="SkyMap"/>.</summary>
    /// <param name="pass">Was die Horizontsuche passieren darf (hier gleich dem Schutz; die Sonne kommt
    /// in <see cref="SkyMap(float[], int, int, float[])"/> dazu). Eine höhere Struktur-Schwelle für
    /// Wolken taugte nicht: Dichter Dunst über weicher Landschaft hat dieselbe schwache Struktur
    /// (0,02–0,06) — mit ihr galt der ganze Testdunst als Himmel.</param>
    private static float[] SmoothBright(float[] lin, int w, int h, out float[] pass)
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

        var sky = new float[l.Length];
        pass = new float[l.Length];
        for (var i = 0; i < sky.Length; i++)
        {
            var structure = band[i] / Math.Max(mean[i], 1e-3f);
            var smooth = 1 - SmoothStep(0.008f, 0.02f, structure);
            var bright = SmoothStep(0.03f, 0.1f, l[i]);   // dunkle glatte Schatten sind kein Himmel
            sky[i] = bright * smooth;
            pass[i] = sky[i];
        }
        return sky;
    }

    /// <summary>Alles unterhalb des Horizonts auf 0 setzen.</summary>
    private static void ReachFromTop(float[] pass, float[] sky, int w, int h, float[] lin)
    {
        // Nur, was vom oberen Bildrand herabhängt, ist Himmel: Dichter Dunst über der Landschaft ist
        // genauso glatt und hell — er wurde als Himmel geschützt, der Dunst am Horizont blieb stehen
        // (gemessen am künstlichen Dunst: Buntheit nur 0,4× der Wahrheit trotz richtiger Dichte).
        // Je Spalte reicht der Himmel bis zur ersten strukturierten Zone; ein paar Zeilen (Vogel,
        // dünner Ast) überbrückt er. Eine Wolke überbrückt er auch: wenn darunter wieder glatter
        // Himmel kommt, ähnlich hell wie der darüber — sonst endete die Linie an der grauen
        // Unterseite, und die Wolke darunter wurde als Landschaft gerechnet (magenta). Dichter Dunst
        // unter dem Horizont ist deutlich dunkler als der Himmel, er zählt nicht als Fortsetzung.
        // Die Grenze wird waagerecht ausgedehnt, damit Äste vom oberen Rand (Bäume am Bildrand) und
        // die Sonne (ihre Strahlen sind strukturiert) den Himmel darunter nicht ungeschützt lassen.
        var horizon = new int[w];
        var gap = Math.Max(2, h / 50);
        var cloud = h * 3 / 10;
        float L(int x, int y) { var i = (y * w + x) * 3; return 0.0722f * lin[i] + 0.7152f * lin[i + 1] + 0.2126f * lin[i + 2]; }
        for (var x = 0; x < w; x++)
        {
            var structured = 0;
            var end = 0;                     // Ende des Himmels (erste Zeile darunter)
            float skySum = 0; var skyRows = 0;
            for (var y = 0; y < h; y++)
            {
                if (pass[y * w + x] >= 0.5f) { structured = 0; end = y + 1; skySum += L(x, y); skyRows++; continue; }
                if (++structured <= gap) continue;

                // Wolke? Darunter wieder Himmel: ein langer glatter Streifen (8 % der Höhe), im Mittel
                // mindestens 85 % so hell wie der Himmel darüber. Dichter Dunst unter dem Horizont
                // erreicht höchstens 65 % — helle Felder darin zwar mehr, aber nur ein paar Zeilen
                // am Stück, dann kommen Hecken und Kanten.
                var meanSky = skyRows > 0 ? skySum / skyRows : 0;
                var resume = -1;
                var run = Math.Max(gap, h * 8 / 100);
                for (var y2 = y + 1; y2 + run <= Math.Min(h, y + cloud); y2++)
                {
                    if (pass[y2 * w + x] < 0.5f) continue;
                    var ok = true; float sum = 0;
                    for (var k = 0; k < run && ok; k++) { if (pass[(y2 + k) * w + x] < 0.5f) ok = false; else sum += L(x, y2 + k); }
                    if (ok && sum / run >= 0.85f * meanSky) resume = y2;
                    break;
                }
                if (resume < 0) break;
                y = resume - 1;
                structured = 0;
            }
            horizon[x] = end;
        }

        var reach = Math.Max(1, w * 12 / 100);
        var dilated = new int[w];
        for (var x = 0; x < w; x++)
        {
            var best = 0;
            for (var k = Math.Max(0, x - reach); k <= Math.Min(w - 1, x + reach); k++) best = Math.Max(best, horizon[k]);
            dilated[x] = best;
        }
        // Oberhalb der Linie ist alles Himmel — auch Wolken, Sonne und ihre Strahlen: strukturiert,
        // aber nie dunstbedeckte Landschaft. Als „Landschaft“ wurden Wolken magenta (die Rückrechnung
        // zog ihnen das Luftlicht ab).
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
            sky[y * w + x] = y < dilated[x] ? 1 : 0;
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
    internal static float[] MinFilter(float[] src, int width, int height, int r) => MinFilter(src, width, height, r, r);

    /// <summary>Minimum über ein Rechteck mit den Radien rx (waagerecht) und ry (senkrecht).</summary>
    internal static float[] MinFilter(float[] src, int width, int height, int rx, int ry)
    {
        var rows = new float[src.Length];
        Parallel.For(0, height, y => SlidingMin(src, rows, y * width, 1, width, rx));
        var dst = new float[src.Length];
        Parallel.For(0, width, x => SlidingMin(rows, dst, x, width, height, ry));
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
