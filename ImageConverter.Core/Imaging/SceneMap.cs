namespace ImageConverter.Core.Imaging;

/// <summary>
/// Szenenkarte: wo Himmel, Lichthof einer Lichtquelle (Sonne) und Landschaft liegen — einmal am
/// Anfang aus dem Bild bestimmt, auf der kleinen Arbeitsfassung (≤ 1024 px), mit weichen Übergängen.
/// Alle Schritte, die Himmel oder Sonne schonen, lesen diese eine Karte, statt eigene Regeln zu
/// rechnen (Phase 3 des Neubaus; bis 0.4.50 steckte sie im ersten Dunst-Durchlauf).
/// </summary>
/// <remarks>
/// Kodierung wie bisher: 0 Landschaft, 1 Himmel, Werte über 1 Lichthof (1 + Hof). Daraus
/// <see cref="Land"/> = 1 − min(1, Himmel) und <see cref="Halo"/> = min(1, max(0, Himmel − 1)).
/// </remarks>
internal sealed class SceneMap
{
    /// <summary>Himmel je Feld der kleinen Fassung (0 … 2, siehe oben).</summary>
    public float[] Sky { get; }
    public int W { get; }
    public int H { get; }

    /// <summary>Ein Feld der Karte entspricht <see cref="Factor"/> × <see cref="Factor"/> Bildpixeln.</summary>
    public int Factor { get; }

    private SceneMap(float[] sky, int w, int h, int factor) => (Sky, W, H, Factor) = (sky, w, h, factor);

    /// <param name="small">Kleine Fassung in linearem Licht (B, G, R je Feld).</param>
    public static SceneMap Of(float[] small, int w, int h, int factor) => new(Dehaze.SkyMap(small, w, h), w, h, factor);

    /// <summary>Himmelswert (0 … 2) an einem Bildpixel, bilinear aus der Karte.</summary>
    public float SkyAt(int x, int y)
    {
        var sx = Math.Clamp((x + 0.5f) / Factor - 0.5f, 0, W - 1);
        var sy = Math.Clamp((y + 0.5f) / Factor - 0.5f, 0, H - 1);
        int x0 = (int)sx, y0 = (int)sy;
        int x1 = Math.Min(x0 + 1, W - 1), y1 = Math.Min(y0 + 1, H - 1);
        float fx = sx - x0, fy = sy - y0;
        var top = Sky[y0 * W + x0] * (1 - fx) + Sky[y0 * W + x1] * fx;
        var bottom = Sky[y1 * W + x0] * (1 - fx) + Sky[y1 * W + x1] * fx;
        return top * (1 - fy) + bottom * fy;
    }

    /// <summary>1 = Landschaft, 0 = Himmel oder Lichthof.</summary>
    public static float Land(float sky) => 1 - Math.Clamp(sky, 0, 1);

    /// <summary>1 = mitten im Lichthof einer Lichtquelle.</summary>
    public static float Halo(float sky) => Math.Clamp(sky - 1, 0, 1);
}
