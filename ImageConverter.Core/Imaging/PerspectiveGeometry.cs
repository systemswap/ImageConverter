namespace ImageConverter.Core.Imaging;

/// <summary>
/// Geometrie fürs Entzerren: Ein Viereck (vier Ecken in Bildpixeln, Reihenfolge oben links, oben rechts,
/// unten rechts, unten links) liegt auf etwas, das in Wirklichkeit rechteckig ist — es wird zum Rechteck.
/// </summary>
public static class PerspectiveGeometry
{
    /// <summary>Kleinste sinnvolle Seite in Pixeln.</summary>
    public const double MinSide = 4;

    /// <summary>
    /// Ist das Viereck brauchbar: vier Ecken, keine Überkreuzung, nicht eingedellt (konvex), nicht zu klein,
    /// im Uhrzeigersinn (oben links → oben rechts → unten rechts → unten links)? Andersherum käme ein
    /// gespiegeltes Bild heraus.
    /// </summary>
    public static bool IsValid(IReadOnlyList<(double X, double Y)> quad)
    {
        if (quad.Count != 4) return false;
        for (var i = 0; i < 4; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % 4];
            var c = quad[(i + 2) % 4];
            if (Distance(a, b) < MinSide) return false;
            // Bildschirm (y nach unten): im Uhrzeigersinn ist das Kreuzprodukt positiv
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross < 1e-6) return false;   // eingedellt, überkreuzt, gespiegelt oder drei Ecken auf einer Linie
        }
        return true;
    }

    /// <summary>
    /// Größe des Ergebnisses: Mittel der gegenüberliegenden Kanten — die nahe (lange) und die ferne (kurze)
    /// Kante gleichen sich aus. Das wahre Seitenverhältnis lässt sich aus vier Punkten allein nicht bestimmen.
    /// </summary>
    public static (int Width, int Height) TargetSize(IReadOnlyList<(double X, double Y)> quad)
    {
        var w = (Distance(quad[0], quad[1]) + Distance(quad[3], quad[2])) / 2;
        var h = (Distance(quad[0], quad[3]) + Distance(quad[1], quad[2])) / 2;
        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    /// <summary>
    /// Projektive Abbildung (Homographie), die <paramref name="from"/>[i] auf <paramref name="to"/>[i] bringt
    /// (je vier Punkte). Ergebnis zeilenweise h0 … h8 mit h8 = 1:
    /// x' = (h0·x + h1·y + h2) / (h6·x + h7·y + 1), y' = (h3·x + h4·y + h5) / (h6·x + h7·y + 1).
    /// </summary>
    public static double[] Homography(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
    {
        // 8 Gleichungen, 8 Unbekannte
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = from[i];
            var (u, v) = to[i];
            var r = 2 * i;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -u * x; a[r, 7] = -u * y; a[r, 8] = u;
            a[r + 1, 3] = x; a[r + 1, 4] = y; a[r + 1, 5] = 1; a[r + 1, 6] = -v * x; a[r + 1, 7] = -v * y; a[r + 1, 8] = v;
        }

        // Gauß mit Spaltenpivot
        for (var col = 0; col < 8; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < 8; row++)
                if (Math.Abs(a[row, col]) > Math.Abs(a[pivot, col])) pivot = row;
            if (Math.Abs(a[pivot, col]) < 1e-12) throw new ArgumentException("Die Punkte liegen auf einer Linie.");
            for (var k = 0; k < 9; k++) (a[col, k], a[pivot, k]) = (a[pivot, k], a[col, k]);
            for (var row = 0; row < 8; row++)
            {
                if (row == col) continue;
                var f = a[row, col] / a[col, col];
                for (var k = col; k < 9; k++) a[row, k] -= f * a[col, k];
            }
        }

        var h = new double[9];
        for (var i = 0; i < 8; i++) h[i] = a[i, 8] / a[i, i];
        h[8] = 1;
        return h;
    }

    /// <summary>Punkt durch die Homographie schicken.</summary>
    public static (double X, double Y) Apply(double[] h, double x, double y)
    {
        var w = h[6] * x + h[7] * y + h[8];
        return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
    }

    /// <summary>
    /// Punkt im Viereck an der Stelle (u, v) des späteren Rechtecks (0 … 1) — perspektivisch richtig. Für das
    /// Hilfsgitter: Linien bei u = ⅓, ⅔ zeigen, wo im Ergebnis die Drittel liegen.
    /// </summary>
    public static (double X, double Y) PointAt(IReadOnlyList<(double X, double Y)> quad, double u, double v)
    {
        var h = Homography([(0, 0), (1, 0), (1, 1), (0, 1)], quad);
        return Apply(h, u, v);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
