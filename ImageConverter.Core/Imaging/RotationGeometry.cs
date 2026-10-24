namespace ImageConverter.Core.Imaging;

/// <summary>
/// Geometry for free rotation.
/// </summary>
/// <remarks>
/// The image (width × height) is rotated about its centre; positive angles rotate clockwise (as in ImageMagick and the
/// preview). Frames are axis-aligned rectangles in image pixels in the <b>unrotated</b> coordinate system of the preview:
/// the origin is the top-left corner of the unrotated image, and the centre of rotation lies at (width/2, height/2).
/// Angles are in degrees.
/// </remarks>
public static class RotationGeometry
{
    /// <summary>
    /// Computes the largest centred rectangle with aspect ratio <paramref name="aspect"/> that lies entirely within the
    /// rotated image, so that no empty corners remain.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="degrees">Rotation angle in degrees, clockwise.</param>
    /// <param name="aspect">Aspect ratio of the rectangle (width/height); must be positive.</param>
    /// <returns>The rectangle in unrotated image coordinates. At 0° and 180° with the image's own aspect ratio it is the
    /// whole image; in between it is smaller (smallest around 45°, or around 90° for landscape images).</returns>
    public static (double X, double Y, double Width, double Height) LargestInside(
        double width, double height, double degrees, double aspect)
    {
        var (cos, sin) = AbsCosSin(degrees);
        // Corner (±w/2, ±h/2) rotated back into the image: |x| = w/2·cos + h/2·sin ≤ width/2, likewise for y.
        var h = Math.Min(width / (aspect * cos + sin), height / (aspect * sin + cos));
        var w = aspect * h;
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>Determines whether a point lies within the rotated image.</summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="degrees">Rotation angle in degrees, clockwise.</param>
    /// <param name="x">X coordinate of the point in unrotated image coordinates.</param>
    /// <param name="y">Y coordinate of the point in unrotated image coordinates.</param>
    /// <param name="tolerance">Tolerance in pixels; points this far outside still count as inside.</param>
    /// <returns>True if the point lies within the rotated image (including the tolerance).</returns>
    public static bool Contains(double width, double height, double degrees, double x, double y, double tolerance = 1e-6)
    {
        var rad = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(rad), Math.Sin(rad));
        var dx = x - width / 2;
        var dy = y - height / 2;
        // Rotate back by −angle into the coordinates of the unrotated image.
        var qx = dx * cos + dy * sin;
        var qy = -dx * sin + dy * cos;
        return Math.Abs(qx) <= width / 2 + tolerance && Math.Abs(qy) <= height / 2 + tolerance;
    }

    /// <summary>
    /// Determines whether a rectangle lies entirely within the rotated image. The rotated image is convex, so checking the
    /// four corners is sufficient.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="degrees">Rotation angle in degrees, clockwise.</param>
    /// <param name="x">Left edge of the rectangle in unrotated image coordinates.</param>
    /// <param name="y">Top edge of the rectangle in unrotated image coordinates.</param>
    /// <param name="w">Width of the rectangle in pixels.</param>
    /// <param name="h">Height of the rectangle in pixels.</param>
    /// <param name="tolerance">Tolerance in pixels, see <see cref="Contains"/>.</param>
    /// <returns>True if all four corners lie within the rotated image.</returns>
    public static bool ContainsRect(double width, double height, double degrees,
        double x, double y, double w, double h, double tolerance = 1e-6) =>
        Contains(width, height, degrees, x, y, tolerance)
        && Contains(width, height, degrees, x + w, y, tolerance)
        && Contains(width, height, degrees, x, y + h, tolerance)
        && Contains(width, height, degrees, x + w, y + h, tolerance);

    /// <summary>
    /// Shrinks a rectangle about its centre until it lies entirely within the rotated image (for example after input in
    /// the number fields). If the centre itself lies outside, it moves to the image centre.
    /// </summary>
    /// <param name="width">Image width in pixels.</param>
    /// <param name="height">Image height in pixels.</param>
    /// <param name="degrees">Rotation angle in degrees, clockwise.</param>
    /// <param name="x">Left edge of the rectangle in unrotated image coordinates.</param>
    /// <param name="y">Top edge of the rectangle in unrotated image coordinates.</param>
    /// <param name="w">Width of the rectangle in pixels.</param>
    /// <param name="h">Height of the rectangle in pixels.</param>
    /// <returns>The unchanged rectangle if it already fits; otherwise the largest scaled copy with the same aspect ratio
    /// that fits, found by bisection (30 steps).</returns>
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

    /// <summary>Normalises an angle to the range (−180, +180] degrees.</summary>
    /// <param name="degrees">Angle in degrees.</param>
    /// <returns>The equivalent angle greater than −180 and at most +180.</returns>
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
