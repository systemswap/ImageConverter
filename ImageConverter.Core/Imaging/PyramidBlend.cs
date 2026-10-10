namespace ImageConverter.Core.Imaging;

/// <summary>
/// Zwei Fassungen eines Bildes mit einer Maske mischen, Stufe für Stufe einer Laplace-Pyramide (Burt und Adelson
/// 1983; Grundlage der Belichtungsverschmelzung nach Mertens 2007 und der Tonwertverarbeitung in Googles HDR+):
/// Grobe Helligkeit wird so großflächig gemischt, feine Kanten nur über wenige Pixel — auch eine weiche Maske gibt
/// so keinen Saum an Kanten.
/// </summary>
internal static class PyramidBlend
{
    /// <summary>Kleinste Kante der gröbsten Stufe.</summary>
    private const int MinTop = 8;

    /// <summary>
    /// Die gröbste Stufe ist höchstens so grob wie ein 24stel der längsten Kante: Weiter oben würde die Maske über das
    /// halbe Bild gemittelt — ein großer heller Bereich bekäme dann Licht vom dunklen daneben ab. Relativ zur Bildgröße,
    /// damit Vorschau und Export gleich aussehen.
    /// </summary>
    private const int TopPerEdge = 24;

    /// <summary>
    /// <paramref name="a"/> · (1 − w) + <paramref name="b"/> · w, je Stufe. Liefert einen neuen Puffer aus dem
    /// <see cref="PixelPool"/>; die Eingaben bleiben unverändert.
    /// </summary>
    public static float[] Blend(float[] a, float[] b, float[] weight, int width, int height)
    {
        var levels = 1;
        var coarsest = Math.Max(MinTop, Math.Max(width, height) / TopPerEdge);
        for (int w = width, h = height; Math.Min(w, h) / 2 >= MinTop && Math.Max(w, h) / 2 >= coarsest && levels < 12;
             w = (w + 1) / 2, h = (h + 1) / 2) levels++;

        var pa = Pyramid.Gaussian(a, width, height, levels);
        var pb = Pyramid.Gaussian(b, width, height, levels);
        var pw = Pyramid.Gaussian(weight, width, height, levels);
        var output = pa.EmptyLike();

        for (var l = 0; l < levels; l++)
        {
            var top = l == levels - 1;
            var la = top ? pa[l] : pa.Laplacian(l);
            var lb = top ? pb[l] : pb.Laplacian(l);
            var wl = pw[l];
            var o = output[l];
            for (var i = 0; i < o.Length; i++) o[i] = la[i] + (lb[i] - la[i]) * Math.Clamp(wl[i], 0, 1);
            if (!top)
            {
                PixelPool.Return(la);
                PixelPool.Return(lb);
            }
        }

        // Stufe 0 der Gauß-Pyramiden sind die Eingaben selbst — nur die gröberen Stufen zurückgeben
        pa.ReturnLevels(1);
        pb.ReturnLevels(1);
        pw.ReturnLevels(1);
        var result = output.Collapse();
        return result;
    }
}
