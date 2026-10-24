namespace ImageConverter.Core.Imaging;

/// <summary>
/// Geometry for perspective correction: a quadrilateral that lies on something rectangular in reality is mapped to a
/// rectangle.
/// </summary>
/// <remarks>
/// A quadrilateral consists of four corners in image pixels in the order top left, top right, bottom right, bottom left.
/// </remarks>
public static class PerspectiveGeometry
{
    /// <summary>Smallest useful side length in pixels.</summary>
    public const double MinSide = 4;

    /// <summary>
    /// Determines whether the quadrilateral is usable: four corners, not self-intersecting, not concave, no side shorter
    /// than <see cref="MinSide"/>, and clockwise (top left → top right → bottom right → bottom left). The opposite order
    /// would produce a mirrored image.
    /// </summary>
    /// <param name="quad">Corners in image pixels.</param>
    /// <returns>True if the quadrilateral can be used for correction.</returns>
    public static bool IsValid(IReadOnlyList<(double X, double Y)> quad)
    {
        if (quad.Count != 4) return false;
        for (var i = 0; i < 4; i++)
        {
            var a = quad[i];
            var b = quad[(i + 1) % 4];
            var c = quad[(i + 2) % 4];
            if (Distance(a, b) < MinSide) return false;
            // Screen coordinates (y pointing down): for clockwise order the cross product is positive.
            var cross = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (cross < 1e-6) return false;   // concave, self-intersecting, mirrored or three corners on one line
        }
        return true;
    }

    /// <summary>
    /// Computes the size of the result as the mean of opposite edges, so that the near (long) and the far (short) edge
    /// balance out. The true aspect ratio cannot be determined from four points alone.
    /// </summary>
    /// <param name="quad">Four corners in image pixels, in the order described in the class remarks.</param>
    /// <returns>Width and height in pixels, at least 1 each.</returns>
    public static (int Width, int Height) TargetSize(IReadOnlyList<(double X, double Y)> quad)
    {
        var w = (Distance(quad[0], quad[1]) + Distance(quad[3], quad[2])) / 2;
        var h = (Distance(quad[0], quad[3]) + Distance(quad[1], quad[2])) / 2;
        return (Math.Max(1, (int)Math.Round(w)), Math.Max(1, (int)Math.Round(h)));
    }

    /// <summary>
    /// Computes the projective mapping (homography) that maps <paramref name="from"/>[i] to <paramref name="to"/>[i].
    /// </summary>
    /// <param name="from">Four source points.</param>
    /// <param name="to">Four target points.</param>
    /// <returns>The matrix row by row as h0 … h8 with h8 = 1:
    /// x' = (h0·x + h1·y + h2) / (h6·x + h7·y + 1), y' = (h3·x + h4·y + h5) / (h6·x + h7·y + 1).</returns>
    /// <exception cref="ArgumentException">The linear system is singular, for example because the points lie on one
    /// line.</exception>
    public static double[] Homography(IReadOnlyList<(double X, double Y)> from, IReadOnlyList<(double X, double Y)> to)
    {
        // 8 equations, 8 unknowns.
        var a = new double[8, 9];
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = from[i];
            var (u, v) = to[i];
            var r = 2 * i;
            a[r, 0] = x; a[r, 1] = y; a[r, 2] = 1; a[r, 6] = -u * x; a[r, 7] = -u * y; a[r, 8] = u;
            a[r + 1, 3] = x; a[r + 1, 4] = y; a[r + 1, 5] = 1; a[r + 1, 6] = -v * x; a[r + 1, 7] = -v * y; a[r + 1, 8] = v;
        }

        // Gaussian elimination with partial (column) pivoting.
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

    /// <summary>Maps a point through a homography.</summary>
    /// <param name="h">Homography from <see cref="Homography"/>.</param>
    /// <param name="x">X coordinate of the point.</param>
    /// <param name="y">Y coordinate of the point.</param>
    /// <returns>The mapped point.</returns>
    public static (double X, double Y) Apply(double[] h, double x, double y)
    {
        var w = h[6] * x + h[7] * y + h[8];
        return ((h[0] * x + h[1] * y + h[2]) / w, (h[3] * x + h[4] * y + h[5]) / w);
    }

    /// <summary>
    /// Returns the point in the quadrilateral that corresponds to position (u, v) of the resulting rectangle, with correct
    /// perspective. Used for the guide grid: lines at u = ⅓ and ⅔ show where the thirds of the result lie.
    /// </summary>
    /// <param name="quad">Four corners in image pixels, in the order described in the class remarks.</param>
    /// <param name="u">Horizontal position in the rectangle, 0 … 1.</param>
    /// <param name="v">Vertical position in the rectangle, 0 … 1.</param>
    /// <returns>The point in image pixels.</returns>
    public static (double X, double Y) PointAt(IReadOnlyList<(double X, double Y)> quad, double u, double v)
    {
        var h = Homography([(0, 0), (1, 0), (1, 1), (0, 1)], quad);
        return Apply(h, u, v);
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
