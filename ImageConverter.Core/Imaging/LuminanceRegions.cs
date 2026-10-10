namespace ImageConverter.Core.Imaging;

/// <summary>
/// Karten für „Gleiche Farbe → Luminanz“ mit Schwellwert und „Ferne schonen“, in kleiner Auflösung gerechnet:
/// <list type="bullet">
/// <item><b>Bereichshelligkeit</b> – die Helligkeit kantentreu eingeebnet (selbstgeführter Guided Filter): Ein Baum
/// ist als Ganzes dunkel, der Himmel als Ganzes hell, die Kante dazwischen bleibt scharf. Der Schwellwert gilt für
/// diese Bereiche, nicht je Pixel – so wird ein dunkler Baum ganz aufgehellt, und neben ihm leuchtet kein Hof in
/// den Himmel.</item>
/// <item><b>Dunst</b> – der dunkelste Farbkanal je Pixel, kantentreu geglättet: Im Vordergrund gibt es immer wirklich
/// dunkle Farbanteile, in dunstiger Ferne nicht. Damit lässt sich dunkle Ferne (Talgrund, Hang)
/// vom dunklen Vordergrund unterscheiden, was die Helligkeit allein nicht kann.</item>
/// </list>
/// </summary>
/// <remarks>
/// Am Panorama mit dunklem Vordergrund ausgewählt (0.5.38): weichgezeichnete Masken gaben deutliche Höfe am Himmel,
/// mehrere Durchläufe (Rolling Guidance) machten die Maske klumpig und brachten die Höfe zurück; die eingeebnete
/// Helligkeit hielt die Pixel neben den Bäumen praktisch unverändert (+0,003 statt +0,31 Oklab-L ohne Grenze).
/// „Ferne schonen“: Der helle Saum am Übergang Ferne → Vordergrund (0.5.38) kam vom Minimumfilter der Dunstkarte, nicht
/// vom Mischen; die fertige Karte kantentreu nachzuführen machte ihn breiter (dort gibt es keine Kante zum Ausrichten).
/// Gemischt wird über eine Laplace-Pyramide (<see cref="PyramidBlend"/>, 0.5.39).
/// </remarks>
/// <summary>Schätzt die Entfernung je Bildteil (KI-Tiefenkarte).</summary>
public interface IDepthEstimator
{
    /// <param name="bgr">Bild klein, B, G, R je Pixel, sRGB 0–1.</param>
    /// <returns>Nähe je Pixel in derselben Größe, 0 = fern … 1 = nah; null, wenn nicht möglich.</returns>
    float[]? Estimate(float[] bgr, int width, int height);
}

public sealed class LuminanceRegions
{
    /// <summary>
    /// Tiefenkarte, falls das Modell geladen ist (setzt das KI-Modul); null = „Ferne schonen“ nimmt den Dunst.
    /// </summary>
    public static IDepthEstimator? Depth { get; set; }

    /// <summary>Nähe, ab der ein Bereich nicht mehr geschont wird (Tiefenkarte 0 = fern … 1 = nah): bis 0,25 voll, ab 0,5 gar nicht.</summary>
    private const float NearFrom = 0.25f, NearTo = 0.5f;

    /// <summary>Längste Kante der Karten — genug für Bereiche, schnell genug für die Vorschau.</summary>
    private const int MapEdge = 1200;

    /// <summary>Breite des Übergangs um den Schwellwert (Oklab-L, nach beiden Seiten).</summary>
    private const float Soft = 0.08f;

    /// <summary>Ab diesem Dunst (dunkelster Kanal, sRGB 0–1) gilt ein Bereich als Ferne; voll ab +0,12.</summary>
    private const float HazeFrom = 0.05f, HazeRange = 0.12f;

    private readonly float[] _base, _haze, _l, _a, _b;
    private readonly float[]? _near;
    private readonly int _w, _h;
    private readonly float _scale;   // Bildpixel je Kartenpixel

    private LuminanceRegions(float[] baseMap, float[] haze, float[]? near, float[] l, float[] a, float[] b, int w, int h, float scale) =>
        (_base, _haze, _near, _l, _a, _b, _w, _h, _scale) = (baseMap, haze, near, l, a, b, w, h, scale);

    /// <summary>Wurde die KI-Tiefenkarte benutzt?</summary>
    public bool UsesDepth => _near is not null;

    /// <summary>Die Karten zu einem Pixelpuffer (B, G, R, A; sRGB 0–1).</summary>
    /// <param name="hazeFrom">Bild für die Dunstkarte von „Ferne“: das Bild vor dem Dunst-Regler. Danach zeigt der Dunst
    /// nicht mehr, was weit weg ist — „Ferne“ schonte mit dem Dunst-Regler zu wenig (bis 0.5.46). null = <paramref name="bgra"/>.</param>
    public static LuminanceRegions Compute(float[] bgra, int width, int height, float[]? hazeFrom = null)
    {
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)MapEdge));
        int w = (width + f - 1) / f, h = (height + f - 1) / f, n = w * h;
        var small = Shrink(bgra, width, height, f, w, h);

        var hazeSmall = hazeFrom is null || ReferenceEquals(hazeFrom, bgra) ? small : Shrink(hazeFrom, width, height, f, w, h);
        var l = new float[n];
        var la = new float[n];
        var lb = new float[n];
        var minChannel = new float[n];
        for (var i = 0; i < n; i++)
        {
            float b = small[i * 3], g = small[i * 3 + 1], r = small[i * 3 + 2];
            (l[i], la[i], lb[i]) = Oklab.FromSrgb(r, g, b);
            minChannel[i] = Math.Min(hazeSmall[i * 3], Math.Min(hazeSmall[i * 3 + 1], hazeSmall[i * 3 + 2]));
        }

        var edge = Math.Max(w, h);
        var radius = Math.Max(2, edge / 60);
        var baseMap = Guided(l, l, w, h, radius, 0.01f);   // Bereiche: Struktur weg, große Kanten bleiben
        // Dunst je Pixel, ohne Minimumfilter: Der „dunkelste Kanal in der Umgebung“ (Dark Channel wie beim Dunst-Regler)
        // trug die dunklen Werte der Büsche ins Tal — dort galt das Tal als nah und hellte als Saum mit auf (0.5.38).
        // Kleiner Glättungsradius: Am Übergang sind Ferne und Vordergrund oft gleich hell, es gibt keine Kante zum Ausrichten.
        var haze = Guided(minChannel, l, w, h, Math.Max(2, edge / 200), 1e-3f);

        // Tiefenkarte (falls geladen): kantentreu an die Bildkanten angelegt — das Modell rechnet mit 518 px
        float[]? near = null;
        if (Depth is { } depth)
        {
            try
            {
                if (depth.Estimate(small, w, h) is { } raw) near = Guided(raw, l, w, h, Math.Max(2, edge / 200), 1e-3f);
            }
            catch (Exception)
            {
                near = null;   // Modell nicht lesbar o. Ä.: Dunst als Ersatz, die Bearbeitung geht weiter
            }
        }
        return new LuminanceRegions(baseMap, haze, near, l, la, lb, w, h, f);
    }

    /// <summary>Verkleinern durch Mitteln je f × f; Ergebnis B, G, R je Pixel.</summary>
    private static float[] Shrink(float[] bgra, int width, int height, int f, int w, int h)
    {
        var small = new float[w * h * 3];
        Parallel.For(0, h, y =>
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

    /// <summary>Wie stark ein Bereich aufgehellt wird (0 … 1), ohne Blick auf den einzelnen Pixel.</summary>
    private float AreaWeight(float b, float haze, float near, float limit, float distance)
    {
        var w = limit < 1 ? 1 - SmoothStep(limit - Soft, limit + Soft, b) : 1;
        if (distance <= 0) return w;
        var far = _near is not null ? 1 - SmoothStep(NearFrom, NearTo, near) : SmoothStep(HazeFrom, HazeFrom + HazeRange, haze);
        return w * (1 - distance * far);
    }

    /// <summary>
    /// Gewichtung für bestimmte Reglerstände (Schwellwert, Ferne). Rechnet einmal die Karte „was liegt dahinter“:
    /// Farbe und Helligkeit der Flächen, die nicht aufgehellt werden (Himmel, Ferne), in die aufgehellten Bereiche
    /// hinein fortgesetzt (normierte Faltung) — damit lassen sich Durchblicke zwischen Ästen erkennen.
    /// </summary>
    /// <param name="limit">Schwellwert als Oklab-L (1 = keine Grenze): hellere Bereiche bleiben.</param>
    /// <param name="distance">Ferne schonen 0 … 1.</param>
    public Weighting For(float limit, float distance)
    {
        var n = _w * _h;
        var excluded = new float[n];
        var el = new float[n];
        var ea = new float[n];
        var eb = new float[n];
        for (var i = 0; i < n; i++)
        {
            var e = 1 - AreaWeight(_base[i], _haze[i], _near?[i] ?? 0, limit, distance);
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
        return new Weighting(this, limit, distance, cover, bl, ba, bb);
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
        private readonly float _limit, _distance;
        private readonly float[] _cover, _behindL, _behindA, _behindB;

        internal Weighting(LuminanceRegions maps, float limit, float distance, float[] cover, float[] l, float[] a, float[] b) =>
            (_maps, _limit, _distance, _cover, _behindL, _behindA, _behindB) = (maps, limit, distance, cover, l, a, b);

        /// <summary>Wie stark ein Pixel aufgehellt wird (0 … 1).</summary>
        /// <param name="l">Oklab des Pixels selbst (L, a, b).</param>
        /// <returns>Gesamt — und der Teil, der echten Kanten folgt (Schwellwert, Schutz, Durchblicke, Tiefenkarte; ohne
        /// den Dunst): die Obergrenze, damit der Himmel neben einem Baum auch beim Verschmelzen über die Pyramide
        /// unverändert bleibt.</returns>
        public (float All, float Cap) Weight(int x, int y, float l, float a, float b)
        {
            var m = _maps;
            var (fx, fy) = m.MapPoint(x, y);
            float area = m.Read(m._base, fx, fy), haze = m.Read(m._haze, fx, fy), near = m._near is null ? 0 : m.Read(m._near, fx, fy);

            var w = _limit < 1 ? 1 - SmoothStep(_limit - Soft, _limit + Soft, area) : 1f;
            if (_limit < 1)
            {
                // Fester Schutz sehr heller Pixel (bleibt als Rückfall, wo nichts „dahinter“ bekannt ist)
                var protect = Math.Max(0.62f, _limit + 0.12f);
                w *= 1 - SmoothStep(protect, protect + 0.18f, l);
            }

            // Durchblick: heller als der Bereich und in der Farbe dessen, was dahinter liegt (Himmel, dunstiges Tal) —
            // nur dieser Anteil bleibt. Sonnige Nadeln sind auch heller, haben aber eine andere Farbe und werden
            // aufgehellt. Vorher wurden Lücken mit aufgehellt: weiße Ränder um Bäume, weißlicher Himmel zwischen Ästen.
            var cover = SmoothStep(0.03f, 0.15f, m.Read(_cover, fx, fy));
            if (cover > 0)
            {
                float bl = m.Read(_behindL, fx, fy), ba = m.Read(_behindA, fx, fy), bb = m.Read(_behindB, fx, fy);
                var share = Math.Clamp((l - area) / Math.Max(0.05f, bl - area), 0, 1);   // Anteil „dahinter“ nach Helligkeit
                // Ein Durchblick (Mischung aus dunklem Ast und Hintergrund) ist nie heller als der Hintergrund — was heller
                // ist, ist sonniges Laub. Ohne diese Grenze galten Blätter vor grünen Wiesen als Durchblick.
                var notBrighter = 1 - SmoothStep(bl + 0.01f, bl + 0.06f, l);
                var chroma = MathF.Sqrt((a - ba) * (a - ba) + (b - bb) * (b - bb));
                var gap = cover * share * notBrighter * (1 - SmoothStep(ChromaSame, ChromaOther, chroma));
                w *= 1 - gap;
            }

            if (_distance <= 0) return (w, w);
            var far = m._near is not null ? 1 - SmoothStep(NearFrom, NearTo, near) : SmoothStep(HazeFrom, HazeFrom + HazeRange, haze);
            var all = w * (1 - _distance * far);
            // Die Tiefenkarte hat echte Kanten (Baum vor Himmel) → gehört zur Obergrenze; der Dunst nicht (dort weich mischen).
            // Ohne das leuchtete bei Schwellwert „aus“ und nur „Ferne“ ein Hof um den Baum in den Himmel (0.5.40, Test).
            return (all, m._near is not null ? all : w);
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
        Parallel.For(0, h, y => Line(src, tmp, y * w, 1, w, r));
        Parallel.For(0, w, x => Line(tmp, dst, x, w, h, r));
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
