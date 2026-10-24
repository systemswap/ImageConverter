namespace ImageConverter.Core.Imaging;

/// <summary>Specifies what a traced line is on the page.</summary>
public enum WarpLineKind
{
    /// <summary>Horizontal on the page: a text line, the top or bottom edge; the curvature turns it into an arc.</summary>
    Horizontal,
    /// <summary>Vertical on the page: a page edge, a column, a table rule.</summary>
    Vertical,
    /// <summary>Fold of a double page: vertical; the paper may have a kink there. Only the first fold line is used as a
    /// fold, further ones count as <see cref="Vertical"/>.</summary>
    Fold,
}

/// <summary>
/// A traced line that is straight in reality.
/// </summary>
/// <param name="Kind">What the line is on the page.</param>
/// <param name="Points">Points relative to the pixel image (x / width, y / height), in the order in which they were
/// placed. Lines with fewer than two points are ignored.</param>
/// <remarks>Equality compares the points element by element (value semantics), so that the record can serve as a cache
/// key.</remarks>
public sealed record WarpLine(WarpLineKind Kind, IReadOnlyList<(double X, double Y)> Points)
{
    /// <summary>Compares kind and points element by element.</summary>
    /// <param name="other">Line to compare with.</param>
    /// <returns><see langword="true"/> if both lines are equal.</returns>
    public bool Equals(WarpLine? other) =>
        other is not null && Kind == other.Kind && Points.SequenceEqual(other.Points);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Kind);
        foreach (var p in Points) h.Add(p);
        return h.ToHashCode();
    }
}

/// <summary>
/// A finished dewarping grid (AI, <c>SyntheticDocDewarp</c>).
/// </summary>
/// <remarks>Independent of the image size, and therefore part of the recipe. Equality compares the arrays element by
/// element.</remarks>
/// <param name="Nx">Number of grid nodes horizontally.</param>
/// <param name="Ny">Number of grid nodes vertically.</param>
/// <param name="Sx">Per node (row by row, <paramref name="Nx"/> · <paramref name="Ny"/> values), the x position in the
/// pixel image, relative (x / width).</param>
/// <param name="Sy">Per node, the y position in the pixel image, relative (y / height).</param>
/// <param name="RelWidth">Width of the result in widths of the pixel image.</param>
/// <param name="RelHeight">Height of the result in widths of the pixel image.</param>
/// <param name="PageAspect">Estimated page aspect ratio (height ÷ width); <see langword="null"/> = unknown.</param>
/// <param name="Din">Whether the aspect ratio was snapped to DIN (1 : √2).</param>
/// <param name="Shading">Shading map of the AI dewarping; <see langword="null"/> = none.</param>
public sealed record DewarpGrid(int Nx, int Ny, float[] Sx, float[] Sy, double RelWidth, double RelHeight, double? PageAspect, bool Din,
    DewarpShading? Shading = null)
{
    /// <summary>Compares all values; the grid arrays element by element.</summary>
    /// <param name="other">Grid to compare with.</param>
    /// <returns><see langword="true"/> if both grids are equal.</returns>
    public bool Equals(DewarpGrid? other) =>
        other is not null && Nx == other.Nx && Ny == other.Ny && RelWidth.Equals(other.RelWidth) && RelHeight.Equals(other.RelHeight)
        && Nullable.Equals(PageAspect, other.PageAspect) && Din == other.Din && Equals(Shading, other.Shading)
        && Sx.AsSpan().SequenceEqual(other.Sx) && Sy.AsSpan().SequenceEqual(other.Sy);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Nx);
        h.Add(Ny);
        h.Add(RelWidth);
        h.Add(RelHeight);
        h.Add(Shading);
        for (var i = 0; i < Sx.Length; i += Math.Max(1, Sx.Length / 16)) { h.Add(Sx[i]); h.Add(Sy[i]); }
        return h.ToHashCode();
    }
}

/// <summary>
/// Shading map of the AI dewarping (SyntheticDoc), already flattened.
/// </summary>
/// <param name="Width">Number of samples horizontally across the result (corner to corner, like the grid nodes).</param>
/// <param name="Height">Number of samples vertically across the result.</param>
/// <param name="Rgb">Per sample (row by row), R, G, B of the brightness the light had there (1 = full light).</param>
/// <param name="Strength">How strongly the shading is removed, 0 … 1 (less for bright photos, as the SyntheticDoc
/// authors do).</param>
/// <param name="On">Whether the shading is removed. The map stays in the recipe when switched off, so that toggling
/// does not require a new AI run.</param>
public sealed record DewarpShading(int Width, int Height, float[] Rgb, double Strength, bool On = true)
{
    /// <summary>Smallest divisor (as used by the SyntheticDoc authors): deep shadows are brightened at most tenfold.</summary>
    public const double MinDivisor = 0.1;

    /// <summary>Samples the light at a position of the result, bilinearly.</summary>
    /// <param name="u">Horizontal position, 0 … 1 (clamped).</param>
    /// <param name="v">Vertical position, 0 … 1 (clamped).</param>
    /// <returns>Light per channel (1 = full light).</returns>
    public (float R, float G, float B) Sample(double u, double v)
    {
        var fx = Math.Clamp(u, 0, 1) * (Width - 1);
        var fy = Math.Clamp(v, 0, 1) * (Height - 1);
        int ix = Math.Min((int)fx, Math.Max(0, Width - 2)), iy = Math.Min((int)fy, Math.Max(0, Height - 2));
        float ax = (float)(fx - ix), ay = (float)(fy - iy);
        int x1 = Math.Min(ix + 1, Width - 1), y1 = Math.Min(iy + 1, Height - 1);
        float At(int x, int y, int c) => Rgb[(y * Width + x) * 3 + c];
        float Lerp(int c) =>
            (At(ix, iy, c) * (1 - ax) + At(x1, iy, c) * ax) * (1 - ay) + (At(ix, y1, c) * (1 - ax) + At(x1, y1, c) * ax) * ay;
        return (Lerp(0), Lerp(1), Lerp(2));
    }

    /// <summary>Computes the factor by which a colour value at the position (u, v) is multiplied:
    /// 1 / (light · strength + 1 − strength), with the divisor at least <see cref="MinDivisor"/>.</summary>
    /// <param name="u">Horizontal position, 0 … 1 (clamped).</param>
    /// <param name="v">Vertical position, 0 … 1 (clamped).</param>
    /// <returns>Gain per channel, at most 1 / <see cref="MinDivisor"/>.</returns>
    public (float R, float G, float B) Gain(double u, double v)
    {
        var (r, g, b) = Sample(u, v);
        float k = (float)Strength;
        static float Inv(float s, float k) => 1f / (float)Math.Max(MinDivisor, s * k + (1 - k));
        return (Inv(r, k), Inv(g, k), Inv(b, k));
    }

    /// <summary>Compares all values; the map element by element.</summary>
    /// <param name="other">Shading map to compare with.</param>
    /// <returns><see langword="true"/> if both maps are equal.</returns>
    public bool Equals(DewarpShading? other) =>
        other is not null && Width == other.Width && Height == other.Height && Strength.Equals(other.Strength) && On == other.On
        && Rgb.AsSpan().SequenceEqual(other.Rgb);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Width);
        h.Add(Height);
        h.Add(Strength);
        h.Add(On);
        for (var i = 0; i < Rgb.Length; i += Math.Max(1, Rgb.Length / 16)) h.Add(Rgb[i]);
        return h.ToHashCode();
    }
}

/// <summary>
/// Describes how to flatten a curved page: the traced lines and the focal length of the shot.
/// </summary>
/// <param name="Lines">Traced lines; only lines with at least two points are used.</param>
/// <param name="Focal35">35 mm equivalent focal length in mm (from the EXIF data, see <see cref="Dewarp.Focal35Of"/>);
/// without one, 28 mm, as is common for phones.</param>
/// <param name="GridOnly">No 3D page model: only straighten the lines (grid), the aspect ratio stays as in the source;
/// for a second pass on an already flattened image.</param>
/// <param name="Grid">Finished grid of an AI dewarping; if set, the lines are not used.</param>
public sealed record DewarpSpec(IReadOnlyList<WarpLine> Lines, double Focal35 = DewarpSpec.DefaultFocal35, bool GridOnly = false,
    DewarpGrid? Grid = null)
{
    /// <summary>Default 35 mm equivalent focal length in mm (main camera of a phone).</summary>
    public const double DefaultFocal35 = 28;

    /// <summary>Gets the lines with at least two points; only these are used.</summary>
    public IEnumerable<WarpLine> UsableLines => Lines.Where(l => l.Points.Count >= 2);

    /// <summary>Gets whether there is anything to compute: a grid or at least one usable line.</summary>
    public bool IsUsable => Grid is not null || UsableLines.Any();

    /// <summary>Compares all values; the lines element by element.</summary>
    /// <param name="other">Specification to compare with.</param>
    /// <returns><see langword="true"/> if both specifications are equal.</returns>
    public bool Equals(DewarpSpec? other) =>
        other is not null && Focal35.Equals(other.Focal35) && GridOnly == other.GridOnly && Equals(Grid, other.Grid)
        && Lines.SequenceEqual(other.Lines);

    public override int GetHashCode()
    {
        var h = new HashCode();
        h.Add(Focal35);
        h.Add(GridOnly);
        h.Add(Grid);
        foreach (var l in Lines) h.Add(l);
        return h.ToHashCode();
    }
}
