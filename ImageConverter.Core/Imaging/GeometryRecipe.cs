using ImageMagick;

namespace ImageConverter.Core.Imaging;

/// <summary>Stages of the geometry recipe; each one can be switched off in the edits list.</summary>
[Flags]
public enum GeometryStages
{
    /// <summary>No stage.</summary>
    None = 0,
    /// <summary>Perspective correction: a quadrilateral becomes an upright rectangle.</summary>
    Straighten = 1,
    /// <summary>Levelling: free rotation about the centre.</summary>
    Level = 2,
    /// <summary>Crop (with rounded corners).</summary>
    Crop = 4,
    /// <summary>Rotation in 90° steps and mirroring.</summary>
    Orient = 8,
    /// <summary>Flattening a curved page (traced lines); the first stage.</summary>
    Dewarp = 16,
    /// <summary>All stages.</summary>
    All = Dewarp | Straighten | Level | Crop | Orient,
}

/// <summary>Quadrilateral in relative coordinates (0 … 1 of the image width or height): top left, top right, bottom
/// right, bottom left.</summary>
public readonly record struct RelQuad(double X0, double Y0, double X1, double Y1, double X2, double Y2, double X3, double Y3)
{
    /// <summary>Converts the corners to pixel coordinates.</summary>
    /// <param name="width">Image width in px.</param>
    /// <param name="height">Image height in px.</param>
    /// <returns>The four corners in px, in the order of the record.</returns>
    public IReadOnlyList<(double X, double Y)> ToPixels(double width, double height) =>
        [(X0 * width, Y0 * height), (X1 * width, Y1 * height), (X2 * width, Y2 * height), (X3 * width, Y3 * height)];

    /// <summary>Creates a relative quadrilateral from pixel coordinates.</summary>
    /// <param name="q">Four corners in px: top left, top right, bottom right, bottom left.</param>
    /// <param name="width">Image width in px.</param>
    /// <param name="height">Image height in px.</param>
    /// <returns>The quadrilateral relative to the image size.</returns>
    public static RelQuad FromPixels(IReadOnlyList<(double X, double Y)> q, double width, double height) =>
        new(q[0].X / width, q[0].Y / height, q[1].X / width, q[1].Y / height,
            q[2].X / width, q[2].Y / height, q[3].X / width, q[3].Y / height);
}

/// <summary>Rectangle in relative coordinates (fractions of width and height).</summary>
public readonly record struct RelRect(double X, double Y, double Width, double Height);

/// <summary>
/// The geometric alignment of an image as a recipe that is not baked into the pixels.
/// </summary>
/// <remarks>
/// <para>The pixel image (original, AI result, object removal) stays unrotated and uncropped; what is displayed and saved
/// is pixel image + recipe. The order is fixed: <b>dewarp → perspective correction → levelling (with crop) →
/// rotate/mirror</b>, regardless of the order in which the tools were used. Each stage can therefore be switched off
/// without losing the others, and a 90° rotation depends on nothing else.</para>
/// <para>Coordinates are stored relatively: the dewarp lines relative to the pixel image, the quadrilateral relative to
/// the image after dewarping, the crop relative to the image after perspective correction. A later stage thus stays
/// roughly in place when an earlier one is switched off, and the same recipe fits a downscaled copy (AI preview).</para>
/// <para>As with <see cref="ImageOps.RotateAndCrop"/>, the crop applies within the frame of the rotation: axis-aligned
/// in the rotated image, origin at the top left of the unrotated image, rotation about the image centre (positive =
/// clockwise). Rotate/mirror: first <see cref="Turns"/> quarter turns clockwise, then mirrored left ↔ right.</para>
/// <para>Instances are immutable; the methods return modified copies.</para>
/// </remarks>
/// <param name="Quad">Quadrilateral of the perspective correction; null = none.</param>
/// <param name="Angle">Free rotation in degrees (positive = clockwise); normalised to (−180, +180] when applied.</param>
/// <param name="Crop">Crop relative to the image after perspective correction; null = none.</param>
/// <param name="CornerRadius">Rounded corners of the crop as a fraction of the shorter side of the crop (0 = square
/// corners).</param>
/// <param name="Turns">Quarter turns clockwise; any integer, used modulo 4.</param>
/// <param name="Flip">Mirror left ↔ right after the quarter turns.</param>
/// <param name="Dewarp">Curved page: traced lines or an AI grid (<see cref="Imaging.Dewarp"/>); null = none.</param>
public sealed record GeometryRecipe(
    RelQuad? Quad = null,
    double Angle = 0,
    RelRect? Crop = null,
    double CornerRadius = 0,
    int Turns = 0,
    bool Flip = false,
    DewarpSpec? Dewarp = null)
{
    /// <summary>The empty recipe: no stage does anything.</summary>
    public static readonly GeometryRecipe None = new();

    /// <summary>Gets whether the recipe equals <see cref="None"/>.</summary>
    public bool IsNone => this == None with { Turns = 0 } && Turns % 4 == 0;

    /// <summary>Gets whether the dewarp stage has usable lines or a grid.</summary>
    public bool HasDewarp => Dewarp is { IsUsable: true };
    /// <summary>Gets whether a perspective quadrilateral is set.</summary>
    public bool HasStraighten => Quad is not null;
    /// <summary>Gets whether a free rotation is set.</summary>
    public bool HasLevel => Math.Abs(Angle) > 1e-9;
    /// <summary>Gets whether a crop is set.</summary>
    public bool HasCrop => Crop is not null;
    /// <summary>Gets whether quarter turns or mirroring are set.</summary>
    public bool HasOrient => NormalTurns != 0 || Flip;

    /// <summary>Gets the quarter turns normalised to 0 … 3.</summary>
    public int NormalTurns => ((Turns % 4) + 4) % 4;

    /// <summary>Gets the stages that do something.</summary>
    public GeometryStages Present =>
        (HasDewarp ? GeometryStages.Dewarp : 0) | (HasStraighten ? GeometryStages.Straighten : 0) | (HasLevel ? GeometryStages.Level : 0)
        | (HasCrop ? GeometryStages.Crop : 0) | (HasOrient ? GeometryStages.Orient : 0);

    // ------------------------------------------------------------ Actions (for the tools)

    /// <summary>Returns the recipe with an additional 90° rotation of the result. After mirroring, a clockwise rotation
    /// of the image is stored internally as an anticlockwise turn.</summary>
    /// <param name="clockwise"><see langword="true"/> to rotate clockwise, otherwise anticlockwise.</param>
    /// <returns>The modified recipe.</returns>
    public GeometryRecipe Rotated(bool clockwise) =>
        this with { Turns = (NormalTurns + ((clockwise ^ Flip) ? 1 : 3)) % 4 };

    /// <summary>Returns the recipe with an additional left ↔ right mirroring of the result.</summary>
    /// <returns>The modified recipe.</returns>
    public GeometryRecipe Flipped() => this with { Flip = !Flip };

    // ------------------------------------------------------------ Execution

    /// <summary>All intermediate sizes and frames for a pixel image of a given size.</summary>
    internal readonly record struct Plan(
        int SourceWidth, int SourceHeight,
        DewarpMap? Map,
        int FlatWidth, int FlatHeight,
        IReadOnlyList<(double X, double Y)>? QuadPixels,
        int StraightWidth, int StraightHeight,
        double Angle,
        (int X, int Y, int W, int H)? Rect,
        double Radius,
        int Turns, bool Flip,
        int Width, int Height);

    internal Plan PlanFor(int width, int height, GeometryStages stages = GeometryStages.All)
    {
        // Dewarp: the flattened pixel image has its own size; everything further applies to this image
        var map = (stages & GeometryStages.Dewarp) != 0 && Dewarp is { IsUsable: true } spec ? Imaging.Dewarp.MapFor(spec, width, height) : null;
        var (fw, fh) = map?.SizeFor(width) ?? (width, height);

        IReadOnlyList<(double X, double Y)>? quad = null;
        int sw = fw, sh = fh;
        if ((stages & GeometryStages.Straighten) != 0 && Quad is { } q)
        {
            var px = q.ToPixels(fw, fh);
            if (PerspectiveGeometry.IsValid(px))
            {
                quad = px;
                (sw, sh) = PerspectiveGeometry.TargetSize(px);
            }
        }

        var angle = (stages & GeometryStages.Level) != 0 ? RotationGeometry.Normalize(Angle) : 0;
        var levelled = Math.Abs(angle) > 1e-9;
        (int X, int Y, int W, int H)? rect = null;
        if ((stages & GeometryStages.Crop) != 0 && Crop is { } c)
        {
            rect = Whole(c.X * sw, c.Y * sh, c.Width * sw, c.Height * sh, sw, sh, clamp: !levelled);
        }
        else if (levelled)
        {
            // Levelled without a (visible) crop: largest rectangle inside the rotated image, otherwise empty corners
            var (x, y, w, h) = RotationGeometry.LargestInside(sw, sh, angle, (double)sw / sh);
            rect = Whole(Math.Ceiling(x + 1), Math.Ceiling(y + 1), Math.Floor(w - 2), Math.Floor(h - 2), sw, sh, clamp: false);
        }

        int cw = rect?.W ?? sw, ch = rect?.H ?? sh;
        var radius = rect is { } r && (stages & GeometryStages.Crop) != 0 ? CornerRadius * Math.Min(r.W, r.H) : 0;
        var turns = (stages & GeometryStages.Orient) != 0 ? NormalTurns : 0;
        var flip = (stages & GeometryStages.Orient) != 0 && Flip;
        var (ow, oh) = turns % 2 == 1 ? (ch, cw) : (cw, ch);
        return new Plan(width, height, map, fw, fh, quad, sw, sh, angle, rect, radius, turns, flip, ow, oh);
    }

    /// <summary>Computes the size after dewarping (before perspective correction) for a pixel image of the given size.</summary>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <returns>Width and height in px; the input size if there is no dewarp map.</returns>
    public (int Width, int Height) FlatSize(int width, int height)
    {
        var p = PlanFor(width, height, GeometryStages.Dewarp);
        return (p.FlatWidth, p.FlatHeight);
    }

    /// <summary>Returns the dewarp map for a pixel image of the given size (aspect ratio, DIN …).</summary>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <returns>The map, or <see langword="null"/> if there is none.</returns>
    public DewarpMap? DewarpMapFor(int width, int height) => PlanFor(width, height, GeometryStages.Dewarp).Map;

    private static (int, int, int, int) Whole(double x, double y, double w, double h, int sw, int sh, bool clamp)
    {
        int ix = (int)Math.Round(x), iy = (int)Math.Round(y), iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
        if (!clamp) return (ix, iy, iw, ih);
        // As in ImageOps.Crop: entirely inside the image
        ix = Math.Clamp(ix, 0, sw - 1);
        iy = Math.Clamp(iy, 0, sh - 1);
        return (ix, iy, Math.Clamp(iw, 1, sw - ix), Math.Clamp(ih, 1, sh - iy));
    }

    /// <summary>Computes the size of the result for a pixel image of the given size.</summary>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="stages">Stages to apply.</param>
    /// <returns>Width and height of the result in px.</returns>
    public (int Width, int Height) SizeFor(int width, int height, GeometryStages stages = GeometryStages.All)
    {
        var p = PlanFor(width, height, stages);
        return (p.Width, p.Height);
    }

    /// <summary>
    /// Applies the recipe to a pixel image.
    /// </summary>
    /// <param name="source">Pixel image; not modified. Size reads are synchronised by locking on it.</param>
    /// <param name="stages">Stages to apply.</param>
    /// <returns>A new image that the caller disposes, or <see langword="null"/> if none of the stages does anything; then
    /// the pixel image itself is the result, and the caller must not dispose it as if it were a new image.</returns>
    public MagickImage? Apply(MagickImage source, GeometryStages stages = GeometryStages.All)
    {
        int width, height;
        lock (source) (width, height) = ((int)source.Width, (int)source.Height);
        var p = PlanFor(width, height, stages);
        if (p.Map is null && p.QuadPixels is null && p.Rect is null && p.Turns == 0 && !p.Flip) return null;

        MagickImage? work = null;
        MagickImage Current() => work ?? source;
        void Replace(MagickImage next)
        {
            work?.Dispose();
            work = next;
        }

        if (p.Map is { } map) Replace(map.Render(Current()));
        if (p.QuadPixels is { } quad) Replace(ImageOps.Straighten(Current(), quad));
        if (p.Rect is { } r)
        {
            Replace(Math.Abs(p.Angle) > 1e-9
                ? ImageOps.RotateAndCrop(Current(), p.Angle, r.X, r.Y, r.W, r.H, p.Radius)
                : ImageOps.Crop(Current(), r.X, r.Y, r.W, r.H, p.Radius));
        }
        if (p.Turns != 0 || p.Flip)
        {
            var next = ImageOps.CloneOf(Current());
            if (p.Turns != 0) next.Rotate(90 * p.Turns);
            if (p.Flip) next.Flop();
            next.ResetPage();
            Replace(next);
        }
        return work;
    }

    // ------------------------------------------------------------ Coordinate conversion

    /// <summary>Maps a point in the result to the point in the pixel image (eyedropper, brush for object removal).</summary>
    /// <param name="x">Horizontal position in the result in px.</param>
    /// <param name="y">Vertical position in the result in px.</param>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="stages">Stages that are applied in the result.</param>
    /// <returns>Position in the pixel image in px.</returns>
    public (double X, double Y) ToSource(double x, double y, int width, int height, GeometryStages stages = GeometryStages.All)
    {
        var p = PlanFor(width, height, stages);
        (x, y) = UnOrient(x, y, p);
        (x, y) = UnLevel(x, y, p);
        if (p.QuadPixels is { } quad)
            (x, y) = PerspectiveGeometry.Apply(PerspectiveGeometry.Homography(Corners(p), quad), x, y);
        if (p.Map is { } map)
        {
            var (rx, ry) = map.Source(x / p.FlatWidth, y / p.FlatHeight);
            (x, y) = (rx * width, ry * height);
        }
        return (x, y);
    }

    /// <summary>Maps a point in the pixel image to the point in the result.</summary>
    /// <param name="x">Horizontal position in the pixel image in px.</param>
    /// <param name="y">Vertical position in the pixel image in px.</param>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="stages">Stages that are applied in the result.</param>
    /// <returns>Position in the result in px.</returns>
    public (double X, double Y) ToResult(double x, double y, int width, int height, GeometryStages stages = GeometryStages.All)
    {
        var p = PlanFor(width, height, stages);
        if (p.Map is { } map)
        {
            var (u, v) = map.Result(x / width, y / height);
            (x, y) = (u * p.FlatWidth, v * p.FlatHeight);
        }
        if (p.QuadPixels is { } quad)
            (x, y) = PerspectiveGeometry.Apply(PerspectiveGeometry.Homography(quad, Corners(p)), x, y);
        (x, y) = Level(x, y, p);
        return Orient(x, y, p);
    }

    private static (double X, double Y)[] Corners(Plan p) =>
        [(0, 0), (p.StraightWidth, 0), (p.StraightWidth, p.StraightHeight), (0, p.StraightHeight)];

    /// <summary>Perspective-corrected image → cropped (rotated) image.</summary>
    private static (double, double) Level(double x, double y, Plan p)
    {
        if (p.Rect is not { } r) return (x, y);
        if (Math.Abs(p.Angle) > 1e-9)
        {
            double cx = p.StraightWidth / 2.0, cy = p.StraightHeight / 2.0, a = p.Angle * Math.PI / 180;
            double dx = x - cx, dy = y - cy;
            (x, y) = (cx + dx * Math.Cos(a) - dy * Math.Sin(a), cy + dx * Math.Sin(a) + dy * Math.Cos(a));
        }
        return (x - r.X, y - r.Y);
    }

    private static (double, double) UnLevel(double x, double y, Plan p)
    {
        if (p.Rect is not { } r) return (x, y);
        x += r.X;
        y += r.Y;
        if (Math.Abs(p.Angle) > 1e-9)
        {
            double cx = p.StraightWidth / 2.0, cy = p.StraightHeight / 2.0, a = -p.Angle * Math.PI / 180;
            double dx = x - cx, dy = y - cy;
            (x, y) = (cx + dx * Math.Cos(a) - dy * Math.Sin(a), cy + dx * Math.Sin(a) + dy * Math.Cos(a));
        }
        return (x, y);
    }

    /// <summary>Size before rotating/mirroring (after the crop).</summary>
    private static (int W, int H) BeforeOrient(Plan p) => p.Rect is { } r ? (r.W, r.H) : (p.StraightWidth, p.StraightHeight);

    private static (double, double) Orient(double x, double y, Plan p) => OrientPoint(x, y, BeforeOrient(p), p.Turns, p.Flip);

    private static (double, double) UnOrient(double x, double y, Plan p) => UnOrientPoint(x, y, BeforeOrient(p), p.Turns, p.Flip);

    /// <summary>Maps a point on an area through quarter turns (clockwise) followed by mirroring.</summary>
    /// <param name="x">Horizontal position before the transformation.</param>
    /// <param name="y">Vertical position before the transformation.</param>
    /// <param name="size">Area before the transformation.</param>
    /// <param name="turns">Quarter turns clockwise; any integer, used modulo 4.</param>
    /// <param name="flip">Mirror left ↔ right after turning.</param>
    /// <returns>Position after the transformation.</returns>
    public static (double X, double Y) OrientPoint(double x, double y, (int W, int H) size, int turns, bool flip)
    {
        var (w, h) = size;
        for (var i = 0; i < ((turns % 4) + 4) % 4; i++)
        {
            (x, y) = (h - y, x);
            (w, h) = (h, w);
        }
        if (flip) x = w - x;
        return (x, y);
    }

    /// <summary>Inverts <see cref="OrientPoint"/>.</summary>
    /// <param name="x">Horizontal position after the transformation.</param>
    /// <param name="y">Vertical position after the transformation.</param>
    /// <param name="size">Area <b>before</b> turning.</param>
    /// <param name="turns">Quarter turns clockwise; any integer, used modulo 4.</param>
    /// <param name="flip">Whether the transformation mirrored left ↔ right.</param>
    /// <returns>Position before the transformation.</returns>
    public static (double X, double Y) UnOrientPoint(double x, double y, (int W, int H) size, int turns, bool flip)
    {
        var n = ((turns % 4) + 4) % 4;
        var (w, h) = n % 2 == 1 ? (size.H, size.W) : size;   // Area after turning
        if (flip) x = w - x;
        for (var i = 0; i < n; i++)
        {
            // Inverse of (x, y) → (h − y, x) on an area that was h × w before
            (x, y) = (y, w - x);
            (w, h) = (h, w);
        }
        return (x, y);
    }

    /// <summary>Maps a rectangle through rotating/mirroring (axis-aligned stays axis-aligned).</summary>
    /// <param name="x">Left edge before the transformation.</param>
    /// <param name="y">Top edge before the transformation.</param>
    /// <param name="w">Width before the transformation.</param>
    /// <param name="h">Height before the transformation.</param>
    /// <param name="size">Area before the transformation.</param>
    /// <param name="turns">Quarter turns clockwise; any integer, used modulo 4.</param>
    /// <param name="flip">Mirror left ↔ right after turning.</param>
    /// <returns>The rectangle after the transformation, with non-negative width and height.</returns>
    public static (double X, double Y, double W, double H) OrientRect(double x, double y, double w, double h, (int W, int H) size, int turns, bool flip)
    {
        var (ax, ay) = OrientPoint(x, y, size, turns, flip);
        var (bx, by) = OrientPoint(x + w, y + h, size, turns, flip);
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));
    }

    /// <summary>Inverts <see cref="OrientRect"/>.</summary>
    /// <param name="x">Left edge after the transformation.</param>
    /// <param name="y">Top edge after the transformation.</param>
    /// <param name="w">Width after the transformation.</param>
    /// <param name="h">Height after the transformation.</param>
    /// <param name="size">Area before turning.</param>
    /// <param name="turns">Quarter turns clockwise; any integer, used modulo 4.</param>
    /// <param name="flip">Whether the transformation mirrored left ↔ right.</param>
    /// <returns>The rectangle before the transformation, with non-negative width and height.</returns>
    public static (double X, double Y, double W, double H) UnOrientRect(double x, double y, double w, double h, (int W, int H) size, int turns, bool flip)
    {
        var (ax, ay) = UnOrientPoint(x, y, size, turns, flip);
        var (bx, by) = UnOrientPoint(x + w, y + h, size, turns, flip);
        return (Math.Min(ax, bx), Math.Min(ay, by), Math.Abs(bx - ax), Math.Abs(by - ay));
    }

    // ------------------------------------------------------------ Tools

    /// <summary>
    /// Crops in the displayed result. If the crop is visible, the new one cuts further into it; if it is switched off (or
    /// there is none), the new one replaces it.
    /// </summary>
    /// <param name="x">Left edge in px of the result (with the shown stages).</param>
    /// <param name="y">Top edge in px of the result.</param>
    /// <param name="w">Width in px of the result.</param>
    /// <param name="h">Height in px of the result.</param>
    /// <param name="radius">Rounded corners in px.</param>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="shown">Stages that are shown in the result.</param>
    /// <returns>The modified recipe. The free rotation stays if <see cref="GeometryStages.Level"/> is shown; otherwise
    /// the new crop applies without it and the angle is set to 0.</returns>
    public GeometryRecipe CroppedInResult(double x, double y, double w, double h, double radius, int width, int height,
        GeometryStages shown)
    {
        var p = PlanFor(width, height, shown);
        // Back to before rotating/mirroring: into the area after the crop
        var (rx, ry, rw, rh) = UnOrientRect(x, y, w, h, BeforeOrient(p), p.Turns, p.Flip);
        double fx, fy;
        if (p.Rect is { } old)
            (fx, fy) = (old.X + rx, old.Y + ry);   // Within the frame of the rotation: a plain offset
        else
            (fx, fy) = (rx, ry);
        var crop = new RelRect(fx / p.StraightWidth, fy / p.StraightHeight, rw / p.StraightWidth, rh / p.StraightHeight);
        // With the rotation switched off, the new crop applies without it, so it is stored without rotation as well
        var angle = (shown & GeometryStages.Level) != 0 ? Angle : 0;
        return this with { Crop = crop, Angle = angle, CornerRadius = radius / Math.Max(1, Math.Min(rw, rh)) };
    }

    /// <summary>
    /// View for the free rotation tool: pixel image dewarped, perspective-corrected and rotated/mirrored, but without free
    /// rotation and crop. Angle and frame are displayed in it (frame in px of this view, as in
    /// <see cref="ImageOps.RotateAndCrop"/>).
    /// </summary>
    public const GeometryStages LevelView = GeometryStages.Dewarp | GeometryStages.Straighten | GeometryStages.Orient;

    /// <summary>Returns the angle and frame of the recipe in the view <see cref="LevelView"/>.</summary>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <returns>Angle in degrees as seen in the view (negated when mirrored) and the frame in px of the view;
    /// frame <see langword="null"/> = no crop.</returns>
    public (double Angle, (double X, double Y, double W, double H)? Frame) LevelInView(int width, int height)
    {
        var p = PlanFor(width, height);
        var angle = Flip ? -p.Angle : p.Angle;
        if (Crop is not { } c) return (angle, null);
        var size = (p.StraightWidth, p.StraightHeight);
        var frame = OrientRect(c.X * p.StraightWidth, c.Y * p.StraightHeight, c.Width * p.StraightWidth, c.Height * p.StraightHeight,
            size, p.Turns, p.Flip);
        return (angle, frame);
    }

    /// <summary>
    /// Takes over the angle and frame from the view <see cref="LevelView"/> (free rotation and crop are replaced).
    /// Rotations about the centre commute with quarter turns; mirroring reverses the direction.
    /// </summary>
    /// <param name="angle">Angle in degrees as seen in the view.</param>
    /// <param name="x">Left edge of the frame in px of the view.</param>
    /// <param name="y">Top edge of the frame in px of the view.</param>
    /// <param name="w">Width of the frame in px.</param>
    /// <param name="h">Height of the frame in px.</param>
    /// <param name="radius">Rounded corners in px.</param>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <returns>The modified recipe.</returns>
    public GeometryRecipe LevelledInView(double angle, double x, double y, double w, double h, double radius, int width, int height)
    {
        var p = PlanFor(width, height, GeometryStages.Dewarp | GeometryStages.Straighten);
        var size = (p.StraightWidth, p.StraightHeight);
        var (rx, ry, rw, rh) = UnOrientRect(x, y, w, h, size, NormalTurns, Flip);
        return this with
        {
            Angle = RotationGeometry.Normalize(Flip ? -angle : angle),
            Crop = new RelRect(rx / p.StraightWidth, ry / p.StraightHeight, rw / p.StraightWidth, rh / p.StraightHeight),
            CornerRadius = radius / Math.Max(1, Math.Min(rw, rh)),
        };
    }

    /// <summary>View for the perspective tool: dewarped and rotated/mirrored; the quadrilateral lies on the image after
    /// dewarping.</summary>
    public const GeometryStages StraightenView = GeometryStages.Dewarp | GeometryStages.Orient;

    /// <summary>
    /// Returns the quadrilateral of the recipe in the view <see cref="StraightenView"/>.
    /// </summary>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="stages">Shown stages; if dewarping is switched on, the quadrilateral lies on the image after it.</param>
    /// <returns>The four corners in px of the view, or <see langword="null"/> if there is no quadrilateral.</returns>
    public IReadOnlyList<(double X, double Y)>? QuadInView(int width, int height, GeometryStages stages = GeometryStages.All)
    {
        if (Quad is not { } q) return null;
        var size = (stages & GeometryStages.Dewarp) != 0 ? FlatSize(width, height) : (width, height);
        return q.ToPixels(size.Item1, size.Item2).Select(pt => OrientPoint(pt.X, pt.Y, size, NormalTurns, Flip)).ToArray();
    }

    /// <summary>Takes over a quadrilateral from the view <see cref="StraightenView"/>, replacing the current one.</summary>
    /// <param name="quadInView">Four corners in px of the view.</param>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="stages">Shown stages; if dewarping is switched on, the quadrilateral lies on the image after it.</param>
    /// <returns>The modified recipe; the corners are reordered to top left, top right, bottom right, bottom left.</returns>
    public GeometryRecipe StraightenedInView(IReadOnlyList<(double X, double Y)> quadInView, int width, int height,
        GeometryStages stages = GeometryStages.All)
    {
        var size = (stages & GeometryStages.Dewarp) != 0 ? FlatSize(width, height) : (width, height);
        var src = quadInView.Select(pt => UnOrientPoint(pt.X, pt.Y, size, NormalTurns, Flip)).ToArray();
        // After mirroring the order of the corners is swapped left ↔ right; odd turns shift it
        var ordered = Reorder(src);
        return this with { Quad = RelQuad.FromPixels(ordered, size.Item1, size.Item2) };
    }

    /// <summary>View for the curved-page tool: the pixel image, only rotated/mirrored; the lines lie on the original.</summary>
    public const GeometryStages DewarpView = GeometryStages.Orient;

    /// <summary>Returns the dewarp lines in the view <see cref="DewarpView"/>.</summary>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <returns>The lines with their points in px of the view; empty if there are none.</returns>
    public IReadOnlyList<(WarpLineKind Kind, IReadOnlyList<(double X, double Y)> Points)> WarpLinesInView(int width, int height) =>
        Dewarp is not { } spec
            ? []
            : spec.Lines.Select(l => (l.Kind, (IReadOnlyList<(double X, double Y)>)l.Points
                .Select(p => OrientPoint(p.X * width, p.Y * height, (width, height), NormalTurns, Flip)).ToArray())).ToArray();

    /// <summary>
    /// Takes over lines from the view <see cref="DewarpView"/>, replacing the current dewarp stage.
    /// </summary>
    /// <param name="lines">Lines with points in px of the view; lines with fewer than two points are dropped.</param>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="focal35">35 mm equivalent focal length of the shot in mm, from the EXIF data.</param>
    /// <returns>The modified recipe; without a usable line, the dewarp stage is removed.</returns>
    public GeometryRecipe DewarpedInView(IEnumerable<(WarpLineKind Kind, IReadOnlyList<(double X, double Y)> Points)> lines,
        int width, int height, double focal35)
    {
        var spec = new DewarpSpec(lines
            .Where(l => l.Points.Count >= 2)
            .Select(l => new WarpLine(l.Kind, l.Points
                .Select(p => UnOrientPoint(p.X, p.Y, (width, height), NormalTurns, Flip))
                .Select(p => (p.X / width, p.Y / height)).ToArray()))
            .ToArray(), focal35);
        return this with { Dewarp = spec.IsUsable ? spec : null };
    }

    /// <summary>
    /// Takes over the grid of an AI dewarping from the view <see cref="DewarpView"/>, replacing the lines. The grid
    /// applies to the rotated/mirrored view, but the stage computes on the pixel image, so nodes, extent and shading map
    /// are turned back.
    /// </summary>
    /// <param name="viewGrid">Grid computed on the view.</param>
    /// <param name="width">Width of the pixel image in px.</param>
    /// <param name="height">Height of the pixel image in px.</param>
    /// <param name="focal35">35 mm equivalent focal length of the shot in mm.</param>
    /// <returns>The modified recipe.</returns>
    public GeometryRecipe AiDewarpedInView(DewarpGrid viewGrid, int width, int height, double focal35)
    {
        var odd = (((NormalTurns % 4) + 4) % 4) % 2 == 1;
        var (viewW, viewH) = odd ? (height, width) : (width, height);
        var view = new DewarpMap(viewGrid.Nx, viewGrid.Ny, viewGrid.Sx, viewGrid.Sy, viewGrid.RelWidth, viewGrid.RelHeight);
        double wv = viewGrid.RelWidth * viewW, hv = viewGrid.RelHeight * viewW;   // Result in the view, px
        var (ws, hs) = odd ? (hv, wv) : (wv, hv);                                  // Result before turning
        var (nx, ny) = odd ? (viewGrid.Ny, viewGrid.Nx) : (viewGrid.Nx, viewGrid.Ny);
        var sx = new float[nx * ny];
        var sy = new float[nx * ny];
        for (var b = 0; b < ny; b++)
        for (var a = 0; a < nx; a++)
        {
            // Position in the result before turning → in the turned view (unit square) → in the view image → in the pixel image
            var (u, v) = OrientPoint(a / (double)(nx - 1), b / (double)(ny - 1), (1, 1), NormalTurns, Flip);
            var (rx, ry) = view.Source(u, v);
            var (px, py) = UnOrientPoint(rx * viewW, ry * viewH, (width, height), NormalTurns, Flip);
            (sx[b * nx + a], sy[b * nx + a]) = ((float)(px / width), (float)(py / height));
        }
        var aspect = viewGrid.PageAspect is { } pa ? (odd ? 1 / pa : pa) : (double?)null;
        // The shading map lies over the result like the grid, so it is reordered the same way
        DewarpShading? shading = null;
        if (viewGrid.Shading is { } vs)
        {
            var (tw, th) = odd ? (vs.Height, vs.Width) : (vs.Width, vs.Height);
            var rgb = new float[tw * th * 3];
            for (var b = 0; b < th; b++)
            for (var a = 0; a < tw; a++)
            {
                var (u, v) = OrientPoint(a / (double)Math.Max(1, tw - 1), b / (double)Math.Max(1, th - 1), (1, 1), NormalTurns, Flip);
                var k = (b * tw + a) * 3;
                (rgb[k], rgb[k + 1], rgb[k + 2]) = vs.Sample(u, v);
            }
            shading = vs with { Width = tw, Height = th, Rgb = rgb };
        }
        var grid = new DewarpGrid(nx, ny, sx, sy, ws / width, hs / width, aspect, viewGrid.Din, shading);
        return this with { Dewarp = new DewarpSpec([], focal35, Grid: grid) };
    }

    /// <summary>Reorders the corners: top left, top right, bottom right, bottom left (by angle around the centroid).</summary>
    private static (double X, double Y)[] Reorder((double X, double Y)[] q)
    {
        double cx = q.Average(p => p.X), cy = q.Average(p => p.Y);
        var sorted = q.OrderBy(p => Math.Atan2(p.Y - cy, p.X - cx)).ToArray();   // From the left via the top, clockwise (y down)
        // Starting point: the corner with the smallest x + y (top left)
        var start = Array.IndexOf(sorted, sorted.OrderBy(p => p.X + p.Y).First());
        return Enumerable.Range(0, 4).Select(i => sorted[(start + i) % 4]).ToArray();
    }
}
