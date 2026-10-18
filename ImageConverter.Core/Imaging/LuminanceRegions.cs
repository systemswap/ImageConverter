using ImageConverter.Core.Platform;
namespace ImageConverter.Core.Imaging;

/// <summary>
/// Karten für „Gleiche Farbe → Luminanz“ mit Schwellwert und Zielwert, in kleiner Auflösung gerechnet: die
/// <b>Bereichshelligkeit</b> — die Helligkeit kantentreu eingeebnet (selbstgeführter Guided Filter). Ein Baum ist als
/// Ganzes dunkel, der Himmel als Ganzes hell, die Kante dazwischen bleibt scharf. Schwellwert und Zielwert gelten für
/// diese Bereiche, nicht je Pixel — so wird ein dunkler Baum ganz aufgehellt, und neben ihm leuchtet kein Hof in den
/// Himmel. Dazu Farbe und Helligkeit klein, für die Karte „was liegt dahinter“ (Durchblicke zwischen Ästen).
/// </summary>
/// <remarks>
/// Am Panorama mit dunklem Vordergrund ausgewählt (0.5.38): weichgezeichnete Masken gaben deutliche Höfe am Himmel,
/// mehrere Durchläufe (Rolling Guidance) machten die Maske klumpig und brachten die Höfe zurück; die eingeebnete
/// Helligkeit hielt die Pixel neben den Bäumen praktisch unverändert (+0,003 statt +0,31 Oklab-L ohne Grenze).
/// Gemischt wird über eine Laplace-Pyramide (<see cref="PyramidBlend"/>, 0.5.39).
/// „Ferne schonen“ (über Dunst bzw. KI-Tiefenkarte, 0.5.38–0.5.49) ist entfallen (0.5.50, Entscheidung des Benutzers):
/// Luminanz entscheidet nach Helligkeit — Sonne und Spiegelungen können auch im Vordergrund liegen; Nähe und Ferne sind
/// dafür das falsche Merkmal. Den Mittelton (Tal) hält der Zielwert ruhig.
/// </remarks>
public sealed class LuminanceRegions
{
    /// <summary>Längste Kante der Karten — genug für Bereiche, schnell genug für die Vorschau.</summary>
    private const int MapEdge = 1200;

    /// <summary>Breite des Übergangs um den Schwellwert (Oklab-L, nach beiden Seiten).</summary>
    private const float Soft = 0.08f;

    private readonly float[] _base, _l, _a, _b;
    private readonly int _w, _h;
    private readonly float _scale;   // Bildpixel je Kartenpixel

    private LuminanceRegions(float[] baseMap, float[] l, float[] a, float[] b, int w, int h, float scale) =>
        (_base, _l, _a, _b, _w, _h, _scale) = (baseMap, l, a, b, w, h, scale);

    /// <summary>Die Karten zu einem Pixelpuffer (B, G, R, A; sRGB 0–1).</summary>
    public static LuminanceRegions Compute(float[] bgra, int width, int height)
    {
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)MapEdge));
        int w = (width + f - 1) / f, h = (height + f - 1) / f, n = w * h;
        var small = Shrink(bgra, width, height, f, w, h);
        var l = new float[n];
        var la = new float[n];
        var lb = new float[n];
        for (var i = 0; i < n; i++) (l[i], la[i], lb[i]) = Oklab.FromSrgb(small[i * 3 + 2], small[i * 3 + 1], small[i * 3]);

        var radius = Math.Max(2, Math.Max(w, h) / 60);
        var baseMap = Guided(l, l, w, h, radius, 0.01f);   // Bereiche: Struktur weg, große Kanten bleiben
        return new LuminanceRegions(baseMap, l, la, lb, w, h, f);
    }

    /// <summary>Verkleinern durch Mitteln je f × f; Ergebnis B, G, R je Pixel.</summary>
    private static float[] Shrink(float[] bgra, int width, int height, int f, int w, int h)
    {
        var small = new float[w * h * 3];
        Parallel.For(0, h, ComputeLimits.Options, y =>
        {
            for (var x = 0; x < w; x++)
            {
                double b = 0, g = 0, r = 0;
                var count = 0;
                for (var yy = y * f; yy < Math.Min(height, (y + 1) * f); yy++)
                for (var xx = x * f; xx < Math.Min(width, (x + 1) * f); xx++)
                {
                    var i = (yy * width + xx) * 4;
                    b += bgra[i]; g += bgra[i + 1]; r += bgra[i + 2];
                    count++;
                }
                var o = (y * w + x) * 3;
                small[o] = (float)(b / count); small[o + 1] = (float)(g / count); small[o + 2] = (float)(r / count);
            }
        });
        return small;
    }

    /// <summary>Anteil des Bereichs am Schwellwert (1 = darunter, wird aufgehellt; 0 = darüber).</summary>
    private static float BelowLimit(float area, float limit) => limit < 1 ? 1 - SmoothStep(limit - Soft, limit + Soft, area) : 1;

    /// <summary>Faktor des Zielwerts für einen Bereich: so viel, wie es bis zum Ziel braucht, 1 … Luminanz.</summary>
    public static float TargetGain(float area, float target, float luminance) =>
        target > 0 ? Math.Clamp(target / Math.Max(area, 0.02f), 1, luminance) : luminance;

    /// <summary>
    /// Gewichtung für feste Reglerstände (Schwellwert, Zielwert, Luminanz). Rechnet einmal die Karte „was liegt
    /// dahinter“: Farbe und Helligkeit der Flächen, die nicht (oder kaum) aufgehellt werden — über dem Schwellwert oder
    /// schon am Zielwert, z. B. Himmel —, in die aufgehellten Bereiche hinein fortgesetzt (normierte Faltung). Damit
    /// lassen sich Durchblicke zwischen Ästen erkennen.
    /// </summary>
    /// <param name="limit">Schwellwert als Oklab-L (1 = keine Grenze): hellere Bereiche bleiben.</param>
    /// <param name="target">Zielwert als Oklab-L (0 = aus).</param>
    /// <param name="luminance">Luminanz-Faktor (&gt; 1).</param>
    public Weighting For(float limit, float target, float luminance)
    {
        var n = _w * _h;
        var excluded = new float[n];
        var el = new float[n];
        var ea = new float[n];
        var eb = new float[n];
        for (var i = 0; i < n; i++)
        {
            var lift = BelowLimit(_base[i], limit);
            if (target > 0 && luminance > 1) lift *= (TargetGain(_base[i], target, luminance) - 1) / (luminance - 1);
            var e = 1 - lift;
            excluded[i] = e;
            el[i] = e * _l[i];
            ea[i] = e * _a[i];
            eb[i] = e * _b[i];
        }
        var r = Math.Max(4, Math.Max(_w, _h) / BehindReach);
        var cover = Box(excluded, _w, _h, r);
        var bl = Box(el, _w, _h, r);
        var ba = Box(ea, _w, _h, r);
        var bb = Box(eb, _w, _h, r);
        for (var i = 0; i < n; i++)
        {
            var c = Math.Max(cover[i], 1e-4f);
            bl[i] /= c; ba[i] /= c; bb[i] /= c;
        }
        return new Weighting(this, limit, cover, bl, ba, bb);
    }

    /// <summary>
    /// Reichweite der Karte „was liegt dahinter“: ein Fünfzigstel der längsten Kante — Durchblicke gibt es am Rand eines
    /// Baums, nicht tief im dichten Busch. Mit einem Zwanzigstel galten sonnige Blätter mitten im Busch als Durchblick.
    /// </summary>
    private const int BehindReach = 50;

    /// <summary>Gewichtung je Pixel für feste Reglerstände (siehe <see cref="For"/>).</summary>
    public sealed class Weighting
    {
        private readonly LuminanceRegions _maps;
        private readonly float _limit;
        private readonly float[] _cover, _behindL, _behindA, _behindB;

        internal Weighting(LuminanceRegions maps, float limit, float[] cover, float[] l, float[] a, float[] b) =>
            (_maps, _limit, _cover, _behindL, _behindA, _behindB) = (maps, limit, cover, l, a, b);

        /// <summary>Wie stark ein Pixel aufgehellt wird (0 … 1), aus Schwellwert, Schutz heller Pixel und Durchblicken.</summary>
        /// <param name="l">Oklab des Pixels selbst (L, a, b).</param>
        public float Weight(int x, int y, float l, float a, float b)
        {
            var m = _maps;
            var (fx, fy) = m.MapPoint(x, y);
            var area = m.Read(m._base, fx, fy);

            var w = BelowLimit(area, _limit);
            if (_limit < 1)
            {
                // Fester Schutz sehr heller Pixel (bleibt als Rückfall, wo nichts „dahinter“ bekannt ist)
                var protect = Math.Max(0.62f, _limit + 0.12f);
                w *= 1 - SmoothStep(protect, protect + 0.18f, l);
            }

            // Durchblick: heller als der Bereich und in der Farbe dessen, was dahinter liegt (Himmel, Tal) — nur dieser
            // Anteil bleibt. Sonnige Nadeln sind auch heller, haben aber eine andere Farbe und werden aufgehellt.
            var cover = SmoothStep(0.03f, 0.15f, m.Read(_cover, fx, fy));
            if (cover > 0)
            {
                float bl = m.Read(_behindL, fx, fy), ba = m.Read(_behindA, fx, fy), bb = m.Read(_behindB, fx, fy);
                var share = Math.Clamp((l - area) / Math.Max(0.05f, bl - area), 0, 1);   // Anteil „dahinter“ nach Helligkeit
                // Ein Durchblick (Mischung aus dunklem Ast und Hintergrund) ist nie heller als der Hintergrund — was heller
                // ist, ist sonniges Laub. Ohne diese Grenze galten Blätter vor grünen Wiesen als Durchblick.
                var notBrighter = 1 - SmoothStep(bl + 0.01f, bl + 0.06f, l);
                var chroma = MathF.Sqrt((a - ba) * (a - ba) + (b - bb) * (b - bb));
                w *= 1 - cover * share * notBrighter * (1 - SmoothStep(ChromaSame, ChromaOther, chroma));
            }
            return w;
        }
    }

    /// <summary>Farbabstand (Oklab a/b) zu „dahinter“: bis hier gleiche Farbe (Durchblick), ab hier eine andere.</summary>
    private const float ChromaSame = 0.03f, ChromaOther = 0.08f;

    /// <summary>Helligkeit des Bereichs an einem Bildpixel (eingeebnet, Oklab-L).</summary>
    public float AreaBrightness(int x, int y)
    {
        var (fx, fy) = MapPoint(x, y);
        return Read(_base, fx, fy);
    }

    private (float X, float Y) MapPoint(int x, int y) =>
        (Math.Clamp((x + 0.5f) / _scale - 0.5f, 0, _w - 1), Math.Clamp((y + 0.5f) / _scale - 0.5f, 0, _h - 1));

    private float Read(float[] m, float fx, float fy)
    {
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, _w - 1), y1 = Math.Min(y0 + 1, _h - 1);
        float tx = fx - x0, ty = fy - y0;
        return (m[y0 * _w + x0] * (1 - tx) + m[y0 * _w + x1] * tx) * (1 - ty) +
               (m[y1 * _w + x0] * (1 - tx) + m[y1 * _w + x1] * tx) * ty;
    }

    private static float SmoothStep(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Guided Filter (He, Sun, Tang 2010) mit grauer Führung; Kastenfilter über laufende Summen.</summary>
    internal static float[] Guided(float[] p, float[] guide, int w, int h, int r, float eps)
    {
        var n = w * h;
        var ii = new float[n];
        var ip = new float[n];
        for (var i = 0; i < n; i++) { ii[i] = guide[i] * guide[i]; ip[i] = guide[i] * p[i]; }
        var mI = Box(guide, w, h, r);
        var mP = Box(p, w, h, r);
        var mII = Box(ii, w, h, r);
        var mIP = Box(ip, w, h, r);
        var a = new float[n];
        var b = new float[n];
        for (var i = 0; i < n; i++)
        {
            var variance = mII[i] - mI[i] * mI[i];
            a[i] = (mIP[i] - mI[i] * mP[i]) / (variance + eps);
            b[i] = mP[i] - a[i] * mI[i];
        }
        var ma = Box(a, w, h, r);
        var mb = Box(b, w, h, r);
        var q = new float[n];
        for (var i = 0; i < n; i++) q[i] = Math.Clamp(ma[i] * guide[i] + mb[i], 0, 1);
        return q;
    }

    /// <summary>Mittelwert über ±r (Rand: nur, was im Bild liegt).</summary>
    private static float[] Box(float[] src, int w, int h, int r)
    {
        var tmp = new float[src.Length];
        var dst = new float[src.Length];
        Parallel.For(0, h, ComputeLimits.Options, y => Line(src, tmp, y * w, 1, w, r));
        Parallel.For(0, w, ComputeLimits.Options, x => Line(tmp, dst, x, w, h, r));
        return dst;
    }

    private static void Line(float[] src, float[] dst, int start, int step, int length, int r)
    {
        double sum = 0;
        var count = 0;
        for (var k = 0; k <= Math.Min(r, length - 1); k++) { sum += src[start + k * step]; count++; }
        for (var k = 0; k < length; k++)
        {
            dst[start + k * step] = (float)(sum / count);
            var add = k + r + 1;
            var remove = k - r;
            if (add < length) { sum += src[start + add * step]; count++; }
            if (remove >= 0) { sum -= src[start + remove * step]; count--; }
        }
    }
}
