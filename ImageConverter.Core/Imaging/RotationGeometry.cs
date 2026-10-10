namespace ImageConverter.Core.Imaging;

/// <summary>
/// Geometrie für freies Drehen. Das Bild (Breite × Höhe) wird um seine Mitte gedreht, positive
/// Winkel im Uhrzeigersinn (wie ImageMagick und die Vorschau). Rahmen sind achsparallele Rechtecke
/// in Bildpixeln im <b>ungedrehten</b> Koordinatensystem der Vorschau: Ursprung oben links am
/// ungedrehten Bild, die Drehmitte liegt bei (Breite/2, Höhe/2).
/// </summary>
public static class RotationGeometry
{
    /// <summary>
    /// Größtes Rechteck mit dem Seitenverhältnis <paramref name="aspect"/> (Breite/Höhe), mittig,
    /// das ganz im gedrehten Bild liegt — keine leeren Ecken. Bei 0° und 180° das ganze Bild;
    /// dazwischen kleiner (am kleinsten um 45° bzw. bei Querformat um 90°).
    /// </summary>
    public static (double X, double Y, double Width, double Height) LargestInside(
        double width, double height, double degrees, double aspect)
    {
        var (cos, sin) = AbsCosSin(degrees);
        // Ecke (±w/2, ±h/2) ins Bild zurückgedreht: |x| = w/2·cos + h/2·sin ≤ Breite/2, ebenso für y
        var h = Math.Min(width / (aspect * cos + sin), height / (aspect * sin + cos));
        var w = aspect * h;
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>Liegt der Punkt im gedrehten Bild? <paramref name="tolerance"/> in Pixeln.</summary>
    public static bool Contains(double width, double height, double degrees, double x, double y, double tolerance = 1e-6)
    {
        var rad = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(rad), Math.Sin(rad));
        var dx = x - width / 2;
        var dy = y - height / 2;
        // Um −Winkel zurückdrehen: in die Koordinaten des ungedrehten Bildes
        var qx = dx * cos + dy * sin;
        var qy = -dx * sin + dy * cos;
        return Math.Abs(qx) <= width / 2 + tolerance && Math.Abs(qy) <= height / 2 + tolerance;
    }

    /// <summary>
    /// Liegt das Rechteck ganz im gedrehten Bild? Das gedrehte Bild ist konvex, also genügen die
    /// vier Ecken.
    /// </summary>
    public static bool ContainsRect(double width, double height, double degrees,
        double x, double y, double w, double h, double tolerance = 1e-6) =>
        Contains(width, height, degrees, x, y, tolerance)
        && Contains(width, height, degrees, x + w, y, tolerance)
        && Contains(width, height, degrees, x, y + h, tolerance)
        && Contains(width, height, degrees, x + w, y + h, tolerance);

    /// <summary>
    /// Verkleinert ein Rechteck um seine Mitte, bis es ganz im gedrehten Bild liegt (z. B. nach
    /// Eingabe in die Zahlenfelder). Liegt schon die Mitte außerhalb, wandert sie zur Bildmitte.
    /// </summary>
    public static (double X, double Y, double Width, double Height) ShrinkInside(
        double width, double height, double degrees, double x, double y, double w, double h)
    {
        if (ContainsRect(width, height, degrees, x, y, w, h)) return (x, y, w, h);

        var cx = x + w / 2;
        var cy = y + h / 2;
        if (!Contains(width, height, degrees, cx, cy)) (cx, cy) = (width / 2, height / 2);

        double lo = 0, hi = 1;
        for (var i = 0; i < 30; i++)
        {
            var mid = (lo + hi) / 2;
            if (ContainsRect(width, height, degrees, cx - w * mid / 2, cy - h * mid / 2, w * mid, h * mid)) lo = mid;
            else hi = mid;
        }
        return (cx - w * lo / 2, cy - h * lo / 2, w * lo, h * lo);
    }

    /// <summary>Winkel auf −180 … +180 Grad.</summary>
    public static double Normalize(double degrees)
    {
        var d = degrees % 360;
        if (d > 180) d -= 360;
        else if (d <= -180) d += 360;
        return d;
    }

    private static (double Cos, double Sin) AbsCosSin(double degrees)
    {
        var rad = degrees * Math.PI / 180;
        return (Math.Abs(Math.Cos(rad)), Math.Abs(Math.Sin(rad)));
    }
}
