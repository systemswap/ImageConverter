using ImageConverter.Core.Localization;

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
    double Neutralize = 0,
    double Warmth = 0,
    // Dunst, Feineinstellung: Durchläufe und die Wahrnehmungsanteile in Prozent.
    // Die Vorgaben sind die Werte, die bis 0.4.40 fest eingebaut waren; Entstufen (Debanding) seit 0.4.44.
    int DehazePasses = Imaging.Dehaze.DefaultPasses,
    double DehazeClarity = Imaging.Dehaze.DefaultClarity,
    double DehazeContrast = Imaging.Dehaze.DefaultCurve,
    double DehazeVibrance = Imaging.Dehaze.DefaultVibrance,
    double DehazeSharpen = Imaging.Dehaze.DefaultSharpen,
    double DehazeEdge = 0,
    double DehazeBrightness = Imaging.Dehaze.DefaultBrightness,
    double DehazeDeband = Imaging.Dehaze.DefaultDeband,
    double DehazeCast = Imaging.Dehaze.DefaultCast,
    // Weißabgleich mit Pipetten (Gruppe „Weißabgleich“): gemittelte Messungen; null = Pipette nicht benutzt
    WbSample? WbNeutral = null,
    WbSample? WbBlack = null,
    WbSample? WbWhite = null,
    // Gleiche Farbe, Luminanz: Schwellwert (Prozent Helligkeit der Bereiche) — hellere Bereiche bleiben, 100 = keine Grenze;
    // Ferne schonen (0–100 %) — dunstige Bereiche werden entsprechend weniger aufgehellt. Beides nur beim Aufhellen.
    double MatchLimit = 100,
    double MatchDistance = 0,
    // Luminanz: Ziel (Prozent Helligkeit) — dunkle Bereiche werden nur bis hierher angehoben (je Bereich, höchstens um
    // den Luminanz-Faktor); 0 = aus, alles im selben Verhältnis wie in Referenzwerkzeug
    double MatchTarget = 0)
{
    /// <summary>Voreinstellung beim Färben: warmes Braun wie ein alter Fotoabzug.</summary>
    public const double SepiaHue = 30;

    public static readonly Adjustments None = new();

    /// <summary>Nichts zu tun — der Farbton zählt nur, wenn auch gefärbt wird, die Dunst-Feineinstellung nur mit Dunst.</summary>
    public bool IsNone => Normalized == None;

    /// <summary>Ohne Einstellungen, die gerade nicht wirken (Farbton ohne Färben, Dunst-Feineinstellung ohne Dunst).</summary>
    private Adjustments Normalized
    {
        get
        {
            var n = Tint == Tint.Color ? this : this with { TintHue = SepiaHue };
            if (MatchLuminance <= 0 || MatchLimit >= 100) n = n with { MatchLimit = 100 };   // wirkt nur beim Aufhellen
            if (MatchLuminance <= 0 || MatchDistance <= 0) n = n with { MatchDistance = 0 };
            if (MatchLuminance <= 0 || MatchTarget <= 0) n = n with { MatchTarget = 0 };
            return Dehaze == 0 ? n with { DehazePasses = None.DehazePasses, DehazeClarity = None.DehazeClarity, DehazeContrast = None.DehazeContrast,
                DehazeVibrance = None.DehazeVibrance, DehazeSharpen = None.DehazeSharpen, DehazeEdge = 0, DehazeBrightness = None.DehazeBrightness, DehazeDeband = None.DehazeDeband, DehazeCast = None.DehazeCast } : n;
        }
    }

    /// <summary>Nur die Teile, von denen <see cref="Adjust.Prepare"/> abhängt — Schlüssel für die Vorschau.</summary>
    public Adjustments PrepareKey => None with
    {
        Shadows = Shadows, Highlights = Highlights, Dehaze = Normalized.Dehaze,
        DehazePasses = Normalized.DehazePasses, DehazeClarity = Normalized.DehazeClarity, DehazeContrast = Normalized.DehazeContrast,
        DehazeVibrance = Normalized.DehazeVibrance, DehazeSharpen = Normalized.DehazeSharpen, DehazeEdge = Normalized.DehazeEdge,
        DehazeBrightness = Normalized.DehazeBrightness, DehazeDeband = Normalized.DehazeDeband, DehazeCast = Normalized.DehazeCast,
        WbNeutral = WbNeutral, WbBlack = WbBlack, WbWhite = WbWhite,
        // Nur ob die Bereichskarten gebraucht werden (und ob mit Tiefenkarte), nicht die Reglerstände — die wirken
        // erst in Apply (flüssig). Wird das Tiefenmodell geladen oder gelöscht, ändert sich der Schlüssel → neu rechnen.
        MatchLimit = NeedsRegions ? 0 : 100,
        MatchDistance = NeedsRegions && LuminanceRegions.Depth is not null ? 1 : 0,
    };

    /// <summary>Braucht Luminanz die Bereichskarten (<see cref="LuminanceRegions"/>)?</summary>
    public bool NeedsRegions => Normalized is { MatchLuminance: > 0 } n && (n.MatchLimit < 100 || n.MatchDistance > 0 || n.MatchTarget > 0);

    /// <summary>Wird ein Weißabgleich gerechnet?</summary>
    public bool HasWhiteBalance => WhiteBalance.IsActive(WbNeutral, WbBlack, WbWhite);
}

/// <summary>
/// Wendet <see cref="Adjustments"/> auf 8-Bit-BGRA-Pixel an — wie <see cref="Sharpen"/>
/// für Vorschau und Export dieselbe Funktion, damit man exportiert, was man gesehen hat.
///
/// Reihenfolge: Weißabgleich (Pipetten) → Dunst → Tiefen/Lichter → Helligkeit → Kontrast → Gleiche Farbe
/// (Ausgleichen, Luminanz) → Dynamik → Sättigung → Färben.
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

    /// <summary>8-Bit-Zugang (Tests, einfache Aufrufer): rechnet in 32 Bit, rundet am Ende mit Dither.</summary>
    public static byte[] Apply(byte[] bgra, int width, int height, Adjustments a, WorkPlan? plan = null) =>
        Pixels.ToBytes(Apply(Pixels.FromBytes(bgra), width, height, a, plan), dither: true);

    /// <param name="px">Pixelpuffer (B, G, R, A, sRGB-kodiert 0–1, siehe <see cref="Pixels"/>); bleibt unverändert.</param>
    public static float[] Apply(float[] px, int width, int height, Adjustments a, WorkPlan? plan = null)
    {
        var prepared = Prepare(px, width, height, a, plan);
        var result = Apply(prepared, width, height, a, plan);
        if (!ReferenceEquals(prepared.Source, px)) PixelPool.Return(prepared.Source);   // Zwischenstand, gehört uns
        return result;
    }

    /// <summary>
    /// Trägt die Schritte von <see cref="Prepare"/> und <see cref="Apply(Prepared, int, int, Adjustments, WorkPlan?)"/>
    /// in den Fortschritt ein — in derselben Reihenfolge, in der sie dort beginnen.
    /// </summary>
    /// <param name="megapixels">Bildgröße, für den geschätzten Aufwand.</param>
    public static void PlanSteps(Adjustments a, WorkPlan plan, double megapixels)
    {
        if (a.IsNone) return;
        var dehaze = Math.Clamp(a.Dehaze / 100, -1, 3);
        if (dehaze < 0)
            plan.Add(Tr.T("Dunst hinzufügen"), WorkPlan.DehazePassCost * megapixels);
        else if (dehaze > 0)
        {
            var passes = Math.Clamp(a.DehazePasses, 1, Dehaze.MaxPasses);
            for (var i = 1; i <= passes; i++)
                plan.Add(Tr.T("Dunst entfernen – Durchlauf {0} von {1}", i, passes), WorkPlan.DehazePassCost * megapixels);
        }
        if (a.Shadows != 0 || a.Highlights != 0)
            plan.Add(Tr.T("Tiefen und Lichter"), WorkPlan.ToneMapCost * megapixels);
        plan.Add(Tr.T("Licht und Farbe"), WorkPlan.ToolsCost * megapixels);
    }

    /// <summary>Ergebnis des teuren Teils: Bild nach „Dunst entfernen“ und die Zielhelligkeit
    /// aus Tiefen/Lichtern (null, wenn beide auf 0).</summary>
    /// <param name="Regions">Bereichskarten für Luminanz mit Schwellwert / Ferne schonen; null = nicht gebraucht.</param>
    public sealed record Prepared(float[] Source, float[]? Toned, LuminanceRegions? Regions = null);

    /// <summary>
    /// Der teure Teil — Dunst entfernen und Tiefen/Lichter über den Local Laplacian Filter.
    /// Hängt nur von Bild, Dunst, Tiefen und Lichtern ab; die Vorschau merkt sich das
    /// Ergebnis, damit die übrigen Regler flüssig bleiben.
    /// </summary>
    public static Prepared Prepare(float[] px, int width, int height, Adjustments a, WorkPlan? plan = null)
    {
        var dehaze = (float)Math.Clamp(a.Dehaze / 100, -1, 3);   // −100 … +300
        var source = px;

        // Weißabgleich zuerst: Er wirkt auf dasselbe Bild, an dem die Pipetten gemessen haben, und alles
        // Weitere (Dunst, Licht, Farbe) arbeitet schon mit den richtigen Farben
        if (a.HasWhiteBalance)
            source = WhiteBalance.Apply(px, width, height, a.WbNeutral, a.WbBlack, a.WbWhite);
        if (dehaze != 0)
        {
            // Durchläufe wie das Auge: Dunst entfernen, das Ergebnis als Ausgang nehmen, wieder
            // schätzen — wo er weg ist, findet die Schätzung nichts mehr, die Ferne wird weiter
            // klar. Luftlicht und Himmel stammen aus dem ersten Durchlauf, der Himmel bleibt danach
            // unverändert (sonst körnig).
            //
            // Alles in 32-Bit-Fließkomma, gerundet wird einmal am Schluss (mit Dither): Mit 8 Bit
            // zwischen den Schritten verlor jeder Durchlauf Zwischenstufen, und der Kontrast zog die
            // übrigen auseinander — bei 3 Durchläufen Treppen im Himmel.
            //
            // Reihenfolge: Erst die Quelle entstufen (die 8-Bit-Stufen des JPEG werden zu Zwischenwerten,
            // bevor der Kontrast sie spreizt), dann je Durchlauf Dunst → Kontrast/Dynamik → Entstufen →
            // Schärfe. Schärfe zuletzt: Vor dem Entstufen hätte sie die Stufenkanten betont.
            var settings = new Dehaze.Settings(
                (float)(a.DehazeClarity / 100), (float)(a.DehazeSharpen / 100), (float)(a.DehazeContrast / 100),
                (float)(a.DehazeVibrance / 100), (float)(a.DehazeBrightness / 100), (float)Math.Clamp(a.DehazeCast / 100, 0, 1));
            var pass = new Dehaze.Pass();
            var deband = (float)Math.Clamp(a.DehazeDeband / 100, 0, 1);
            // Zwischenstände kommen aus dem Pool und gehen dorthin zurück, sobald der nächste da ist —
            // nie der Eingang (gehört dem Aufrufer, in der Vorschau liegt er im Zwischenspeicher)
            float[] Replace(float[] old, float[] next)
            {
                if (!ReferenceEquals(old, next) && !ReferenceEquals(old, px)) PixelPool.Return(old);
                return next;
            }
            var work = Deband.Apply(source, width, height, deband * Dehaze.SourceDebandShare, seed: 0);
            plan?.Next();   // Entstufen der Quelle zählt zum ersten Schritt
            if (dehaze < 0)
            {
                work = Replace(work, Dehaze.Apply(work, width, height, dehaze, pass, settings, 1f));   // Dunst hinzufügen: ein Schritt
            }
            else
            {
                // Je Durchlauf ein sachter Schritt: Dunst weg, dann Kontrast, Dynamik und Schärfe auf das
                // Ergebnis — der nächste Durchlauf schätzt den Rest neu. Mehr Durchläufe bauen darauf auf.
                // Mit der vollen Stärke je Durchlauf (0.4.43) war schon bei 2 Durchläufen und Dunst 60
                // Schluss, die Dynamik viel zu stark.
                var passes = Math.Clamp(a.DehazePasses, 1, Dehaze.MaxPasses);
                var t = dehaze;   // 0 … 3: Reglerstand / 100
                // Physik und Look getrennt (Phase 4): Der Dunstfilter rechnet nur das Modell (Luftlicht,
                // Durchlässigkeit, Rückrechnung, Helligkeitsausgleich, Farbe), der Look folgt an einer
                // Stelle — je Feinregler ein Mechanismus, Himmel und Lichthof über die Szenenkarte geschont.
                var physics = settings with { Clarity = 0, Sharpen = 0, Curve = 0, Vibrance = 0 };
                var look = new Dehaze.LookSettings(
                    Clarity: (float)(a.DehazeClarity / 100 * Dehaze.LookClarity * t),
                    Curve: (float)(a.DehazeContrast / 100 * Dehaze.LookCurve * t),
                    Vibrance: (float)(a.DehazeVibrance / 100 * Dehaze.LookVibrance * t),
                    VibranceSky: (float)(a.DehazeVibrance / 100 * Dehaze.LookVibranceSky * t),
                    Sharpen: (float)(a.DehazeSharpen / 100 * Dehaze.LookSharpen * t),
                    Edge: (float)(Math.Clamp(a.DehazeEdge / 100, 0, 1) * Dehaze.LoopEdgeShare * t));
                for (var i = 0; i < passes; i++)
                {
                    if (i > 0) plan?.Next();
                    work = Replace(work, Dehaze.Apply(work, width, height, (float)(Dehaze.PassAmount * t), pass, physics, 1f));
                    Dehaze.LookTone(work, width, height, pass.Scene, look);
                    work = Replace(work, Deband.Apply(work, width, height, deband, seed: (uint)i + 1));
                    Dehaze.LookDetail(work, width, height, pass.Scene, look);
                }
            }
            source = work;   // bleibt in 32 Bit; gerundet wird erst für Anzeige oder Datei
        }
        if (a.Shadows != 0 || a.Highlights != 0) plan?.Next();
        return new Prepared(source, ToneMap(source, width, height, a),
            a.NeedsRegions ? LuminanceRegions.Compute(source, width, height) : null);
    }

    /// <summary>Kurve zwischen ihren Stützstellen linear gelesen — ohne Rundung auf eine Stufe.</summary>
    private static float Lookup(float[] curve, float v)
    {
        var x = Math.Clamp(v, 0, 1) * (CurveSize - 1);
        var i = (int)x;
        if (i >= CurveSize - 1) return curve[CurveSize - 1];
        return curve[i] + (curve[i + 1] - curve[i]) * (x - i);
    }

    private static float[]? ToneMap(float[] bgra, int width, int height, Adjustments a)
    {
        var shadows = (float)Math.Clamp(a.Shadows / 100, -1, 1);
        var highlights = (float)Math.Clamp(a.Highlights / 100, -1, 1);
        return shadows != 0 || highlights != 0
            ? ShadowsHighlights(bgra, width, height, shadows, highlights)
            : null;
    }

    /// <param name="prepared">Ergebnis von <see cref="Prepare"/> für dasselbe Bild.</param>
    public static float[] Apply(Prepared prepared, int width, int height, Adjustments a, WorkPlan? plan = null)
    {
        var (bgra, toned, regions) = (prepared.Source, prepared.Toned, prepared.Regions);
        ArgumentOutOfRangeException.ThrowIfNotEqual(bgra.Length, width * height * 4, nameof(prepared));

        var result = PixelPool.Rent(bgra.Length);
        Array.Copy(bgra, result, bgra.Length);
        if (a.IsNone) return result;
        plan?.Next();

        var saturation = (float)Math.Clamp(a.Saturation / 100, -1, 1);
        var vibrance = (float)Math.Clamp(a.Vibrance / 100, -1, 1);
        var curve = ToneCurve(a.Brightness / 100, a.Contrast / 100);
        // Wie Referenzwerkzeug 1–200 %, darüber bis 400 % (Wunsch 0.5.43): sinnvoll mit Zielwert — der Faktor ist dann nur die
        // Obergrenze, tiefe Schatten (8 %) brauchen ×5, um 40 % zu erreichen
        var luminance = Math.Max(0.01f, 1 + (float)Math.Clamp(a.MatchLuminance / 100, -1, 3));
        var limit = (float)Math.Clamp(a.MatchLimit / 100, 0.05, 1);
        var distance = (float)Math.Clamp(a.MatchDistance / 100, 0, 1);
        var target = a.MatchTarget > 0 ? (float)Math.Clamp(a.MatchTarget / 100, 0.05, 1) : 0f;
        if (luminance <= 1 || !a.NeedsRegions) regions = null;   // Schwellwert und Ferne nur beim Aufhellen
        // Gleiche Farbe, Ausgleichen (0–100 %) und Farbe (kühl − … + warm): als Verschiebung in Oklab
        var neutralize = (float)Math.Clamp(a.Neutralize / 100, 0, 1);
        var (castA, castB) = neutralize > 0 ? ColourCast(bgra, width, height) : (0f, 0f);
        var warmth = (float)Math.Clamp(a.Warmth / 100, -1, 1);
        var shiftA = -castA * neutralize + warmth * WarmthA;
        var shiftB = -castB * neutralize + warmth * WarmthB;
        // Reihenfolge wie in Referenzwerkzeug: erst Weißabgleich und Belichtung (Gleiche Farbe), dann Kontrastkurve, dann
        // Dynamik/Sättigung — ein Farbstich soll weg sein, bevor Kontrast und Buntheit ihn verstärken (0.5.27)
        var balance = luminance != 1 || shiftA != 0 || shiftB != 0;
        var chroma = vibrance != 0 || saturation != 0;

        // Pixel nach Tiefen/Lichtern (Zielhelligkeit aus Prepare, Farbton bleibt)
        (float R, float G, float B) Toned(int p)
        {
            var i = p * 4;
            float b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (toned is null) return (r, g, b);
            var lum = Kr * r + Kg * g + Kb * b;
            var target = Math.Clamp(toned[p], 0, 1);
            if (lum <= 1f / 512) return (target, target, target);   // fast schwarz: kein Farbton zu erhalten
            var f = Math.Min(target / lum, 8f);                       // Farbton behalten: alle Kanäle im selben Verhältnis
            return (r * f, g * f, b * f);
        }

        // Luminanz mit Schwellwert / Ferne schonen: Original und aufgehellte Fassung je Größenstufe gemischt
        // (Laplace-Pyramide, wie die Belichtungsverschmelzung nach Mertens bzw. in Googles HDR+) — grobe Helligkeit
        // über große Flächen, Kanten über wenige Pixel. Pixelweise gemischt blieb an weichen Maskenrändern ein
        // heller Saum (0.5.38).
        var lifted = regions is not null ? LiftByRegions(Toned, width, height, luminance, regions, limit, distance, target) : null;

        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var i = p * 4;
                var (r, g, b) = Toned(p);

                if (balance)
                {
                    // Gleiche Farbe in Oklab (vor der Kontrastkurve, wie der Weißabgleich in Referenzwerkzeug)
                    var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                    if (shiftA != 0 || shiftB != 0)
                    {
                        // Ausgleichen + Farbe. Ganz Dunkles bleibt neutral, sonst würde Schwarz farbig.
                        var w = Math.Min(1, L / 0.3f);
                        oa += shiftA * w;
                        ob += shiftB * w;
                    }
                    var factor = 1f;
                    if (luminance != 1 && L > 1e-4f)
                    {
                        // Luminanz: wie mehr Licht — Helligkeit und Buntheit im selben Verhältnis (in Oklab
                        // exakt eine stärkere Beleuchtung). Nur die Helligkeit zu heben ließ die Farben blass wirken.
                        var target = lifted is not null ? Math.Clamp(lifted[p], 0, 1) : Shoulder(L * luminance);
                        factor = target / L;
                        L = target;
                    }
                    (r, g, b) = Oklab.ScaleChroma(L, oa, ob, factor);
                }

                r = Lookup(curve, r); g = Lookup(curve, g); b = Lookup(curve, b);

                if (chroma)
                {
                    // In Oklab: Buntheit ändern lässt Farbton und Helligkeit stehen
                    var (L, oa, ob) = Oklab.FromSrgb(r, g, b);
                    var factor = 1f;
                    if (vibrance != 0) factor *= VibranceFactor(L, oa, ob, vibrance);
                    if (saturation != 0) factor *= 1 + saturation;
                    (r, g, b) = Oklab.ScaleChroma(L, oa, ob, factor);
                }
                if (a.Tint != Tint.None)
                    (r, g, b) = Colorize(Math.Clamp(Kr * r + Kg * g + Kb * b, 0, 1), a.Tint, (float)a.TintHue);

                result[i] = Math.Clamp(b, 0, 1);
                result[i + 1] = Math.Clamp(g, 0, 1);
                result[i + 2] = Math.Clamp(r, 0, 1);
            }
        });

        if (lifted is not null) PixelPool.Return(lifted);
        return result;
    }

    /// <summary>
    /// Zielhelligkeit (Oklab-L) für Luminanz mit Bereichen: Original und voll aufgehellte Fassung, je Größenstufe einer
    /// Laplace-Pyramide mit der geglätteten Bereichsmaske gemischt (Burt/Adelson, wie bei der Belichtungsverschmelzung).
    /// </summary>
    /// <param name="target">Ziel als Oklab-L (0 = aus): Faktor je Bereich so, dass er bis hierher angehoben wird —
    /// höchstens <paramref name="luminance"/>, nie unter 1.</param>
    private static float[] LiftByRegions(Func<int, (float R, float G, float B)> pixel, int width, int height,
        float luminance, LuminanceRegions regions, float limit, float distance, float target)
    {
        var n = width * height;
        var plain = PixelPool.Rent(n);
        var bright = PixelPool.Rent(n);
        var weight = PixelPool.Rent(n);
        var cap = PixelPool.Rent(n);
        var weighting = regions.For(limit, distance);
        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
            {
                var (r, g, b) = pixel(p);
                var (l, oa, ob) = Oklab.FromSrgb(r, g, b);
                // Ziel: je Bereich (nicht je Pixel — die Struktur im Baum bleibt) nur so stark, wie es bis zum Ziel braucht;
                // ein dunkler Baum bekommt viel, eine Wiese im Mittelton kaum etwas
                var gain = target > 0 ? Math.Clamp(target / Math.Max(regions.AreaBrightness(x, y), 0.02f), 1, luminance) : luminance;
                var lift = l > 1e-4f ? Shoulder(l * gain) : l;
                var (all, byEdges) = weighting.Weight(x, y, l, oa, ob);
                plain[p] = l;
                bright[p] = lift;
                weight[p] = all;
                cap[p] = l + (lift - l) * byEdges;
            }
        });
        // Die Pyramide mischt grobe Helligkeit großflächig — auch über echte Kanten hinweg (Himmel neben dem Baum würde
        // heller, der bekannte großflächige Hof der Belichtungsverschmelzung). Schwellwert, Schutz und Tiefenkarte folgen
        // echten Kanten und sind deshalb die Obergrenze; frei weich gemischt wird nur „Ferne“ über den Dunst.
        var blended = PyramidBlend.Blend(plain, bright, weight, width, height);
        Parallel.For(0, n, p => blended[p] = Math.Clamp(blended[p], plain[p], Math.Max(plain[p], cap[p])));
        PixelPool.Return(plain);
        PixelPool.Return(bright);
        PixelPool.Return(weight);
        PixelPool.Return(cap);
        return blended;
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
    private static float[] ShadowsHighlights(float[] bgra, int width, int height, float shadows, float highlights)
    {
        var lum = new float[width * height];
        Parallel.For(0, height, y =>
        {
            for (int x = 0, p = y * width; x < width; x++, p++)
                lum[p] = Kb * bgra[p * 4] + Kg * bgra[p * 4 + 1] + Kr * bgra[p * 4 + 2];
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
    internal static (float A, float B) ColourCast(float[] bgra, int width, int height)
    {
        double sa = 0, sb = 0, n = 0;
        for (var y = 0; y < height; y += 4)
        for (var x = 0; x < width; x += 4)
        {
            var i = (y * width + x) * 4;
            var (L, a, b) = Oklab.FromSrgb(bgra[i + 2], bgra[i + 1], bgra[i]);
            if (L < 0.15f || L > 0.97f || a * a + b * b > 0.1f * 0.1f) continue;
            sa += a; sb += b; n++;
        }
        return n > 0 ? ((float)(sa / n), (float)(sb / n)) : (0, 0);
    }

    /// <summary>
    /// Regler Farbe bei ±100: Verschiebung in Oklab entlang der Farbtemperatur — b nach
    /// Gelb (warm) bzw. Blau (kühl), a etwas mit (Kunstlicht ist gelb-rötlich).
    /// </summary>
    private const float WarmthB = 0.04f, WarmthA = 0.008f;

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
}
