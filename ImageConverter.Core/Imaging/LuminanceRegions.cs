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
public sealed class LuminanceRegions
{
    /// <summary>Längste Kante der Karten — genug für Bereiche, schnell genug für die Vorschau.</summary>
    private const int MapEdge = 1200;

    /// <summary>Breite des Übergangs um den Schwellwert (Oklab-L, nach beiden Seiten).</summary>
    private const float Soft = 0.08f;

    /// <summary>Ab diesem Dunst (dunkelster Kanal, sRGB 0–1) gilt ein Bereich als Ferne; voll ab +0,12.</summary>
    private const float HazeFrom = 0.05f, HazeRange = 0.12f;

    private readonly float[] _base, _haze;
    private readonly int _w, _h;
    private readonly float _scale;   // Bildpixel je Kartenpixel

    private LuminanceRegions(float[] baseMap, float[] haze, int w, int h, float scale) =>
        (_base, _haze, _w, _h, _scale) = (baseMap, haze, w, h, scale);

    /// <summary>Die Karten zu einem Pixelpuffer (B, G, R, A; sRGB 0–1).</summary>
    public static LuminanceRegions Compute(float[] bgra, int width, int height)
    {
        var f = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)MapEdge));
        int w = (width + f - 1) / f, h = (height + f - 1) / f, n = w * h;

        // Verkleinern durch Mitteln je f × f
        var small = new float[n * 3];
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

        var l = new float[n];
        var minChannel = new float[n];
        for (var i = 0; i < n; i++)
        {
            float b = small[i * 3], g = small[i * 3 + 1], r = small[i * 3 + 2];
            l[i] = Oklab.FromSrgb(r, g, b).L;
            minChannel[i] = Math.Min(b, Math.Min(g, r));
        }

        var edge = Math.Max(w, h);
        var radius = Math.Max(2, edge / 60);
        var baseMap = Guided(l, l, w, h, radius, 0.01f);   // Bereiche: Struktur weg, große Kanten bleiben
        // Dunst je Pixel, ohne Minimumfilter: Der „dunkelste Kanal in der Umgebung“ (Dark Channel wie beim Dunst-Regler)
        // trug die dunklen Werte der Büsche ins Tal — dort galt das Tal als nah und hellte als Saum mit auf (0.5.38).
        // Kleiner Glättungsradius: Am Übergang sind Ferne und Vordergrund oft gleich hell, es gibt keine Kante zum Ausrichten.
        var haze = Guided(minChannel, l, w, h, Math.Max(2, edge / 200), 1e-3f);
        return new LuminanceRegions(baseMap, haze, w, h, f);
    }

    /// <summary>
    /// Wie stark ein Pixel aufgehellt wird (0 … 1).
    /// </summary>
    /// <param name="limit">Schwellwert als Oklab-L (1 = keine Grenze): hellere Bereiche bleiben.</param>
    /// <param name="distance">Ferne schonen 0 … 1: dunstige Bereiche entsprechend weniger.</param>
    /// <param name="l">Helligkeit des Pixels selbst — sehr helle Pixel in dunklen Bereichen (Himmel zwischen
    /// Ästen) bleiben, sonst leuchteten die Lücken auf.</param>
    /// <returns>Gesamt — und der Teil aus Schwellwert und Schutz allein (folgt echten Kanten; als Obergrenze, damit
    /// der Himmel neben einem Baum auch beim Verschmelzen über die Pyramide unverändert bleibt).</returns>
    public (float All, float Limit) Weight(int x, int y, float l, float limit, float distance)
    {
        var (b, haze) = Sample(x, y);
        var w = 1f;
        if (limit < 1)
        {
            w = 1 - SmoothStep(limit - Soft, limit + Soft, b);
            var protect = Math.Max(0.62f, limit + 0.12f);
            w *= 1 - SmoothStep(protect, protect + 0.18f, l);
        }
        var all = distance > 0 ? w * (1 - distance * SmoothStep(HazeFrom, HazeFrom + HazeRange, haze)) : w;
        return (all, w);
    }

    /// <summary>Beide Karten an einem Bildpixel, bilinear gelesen.</summary>
    private (float Base, float Haze) Sample(int x, int y)
    {
        var fx = Math.Clamp((x + 0.5f) / _scale - 0.5f, 0, _w - 1);
        var fy = Math.Clamp((y + 0.5f) / _scale - 0.5f, 0, _h - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, _w - 1), y1 = Math.Min(y0 + 1, _h - 1);
        float tx = fx - x0, ty = fy - y0;
        float Read(float[] m) =>
            (m[y0 * _w + x0] * (1 - tx) + m[y0 * _w + x1] * tx) * (1 - ty) +
            (m[y1 * _w + x0] * (1 - tx) + m[y1 * _w + x1] * tx) * ty;
        return (Read(_base), Read(_haze));
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
