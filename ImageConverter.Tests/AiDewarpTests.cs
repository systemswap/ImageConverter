using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>
/// Tests for „KI begradigen“ with a finished grid in the recipe (<see cref="DewarpGrid"/>), without running the model,
/// which would need the ONNX file.
/// </summary>
public class AiDewarpTests
{
    /// <summary>Builds an nx × ny grid mapping result (u, v) → image (x0 + ax·u, y0 + ay·v), in relative coordinates.</summary>
    private static DewarpGrid Affine(int nx, int ny, double x0, double ax, double y0, double ay, double relW, double relH, double? aspect)
    {
        var sx = new float[nx * ny];
        var sy = new float[nx * ny];
        for (var j = 0; j < ny; j++)
        for (var i = 0; i < nx; i++)
            (sx[j * nx + i], sy[j * nx + i]) = ((float)(x0 + ax * i / (nx - 1)), (float)(y0 + ay * j / (ny - 1)));
        return new DewarpGrid(nx, ny, sx, sy, relW, relH, aspect, aspect is not null && Math.Abs(aspect.Value - Math.Sqrt(2)) < 1e-9);
    }

    [Fact]
    public void Grid_BecomesTheMapAsItIs()
    {
        var grid = Affine(5, 4, 0, 1, 0, 1, relW: 0.8, relH: 1.0, aspect: 1.25);
        var spec = new DewarpSpec([], Grid: grid);
        Assert.True(spec.IsUsable);
        Assert.Equal(spec, new DewarpSpec([], Grid: Affine(5, 4, 0, 1, 0, 1, 0.8, 1.0, 1.25)));   // same grid = same recipe (cache key)

        var map = Dewarp.MapFor(spec, 1000, 800);
        Assert.NotNull(map);
        Assert.True(map.Ai);
        Assert.True(map.Modelled);
        Assert.Equal(1.25, map.PageAspect);
        Assert.False(map.Din);
        Assert.Equal((800, 1000), map.SizeFor(1000));
        var (x, y) = map.Source(0.5, 0.25);
        Assert.Equal(0.5, x, 6);
        Assert.Equal(0.25, y, 6);
    }

    [Fact]
    public void ViewGrid_TurnedBack_FitsThePixelImage()
    {
        // Pixel image 1000 × 600, view rotated by a quarter turn (600 × 1000). In the view the grid maps the result
        // onto the middle part: result (u, v) → view (0.1 + 0.8 u, 0.2 + 0.6 v); the result is 480 × 600 px in the
        // view (in view widths: 0.8 and 1.0).
        var recipe = new GeometryRecipe(Turns: 1);
        var view = Affine(5, 4, 0.1, 0.8, 0.2, 0.6, relW: 0.8, relH: 1.0, aspect: 1.25);
        var next = recipe.AiDewarpedInView(view, 1000, 600, 28);
        Assert.True(next.HasDewarp);
        var map = next.DewarpMapFor(1000, 600);
        Assert.NotNull(map);

        // before the rotation the result lies sideways: 600 × 480, aspect ratio inverted
        Assert.Equal((600, 480), map.SizeFor(1000));
        Assert.Equal(0.8, map.PageAspect!.Value, 6);

        // a point of the result before the rotation ↔ the same point in the view ↔ pixel image
        void Check(double us, double vs)
        {
            var (uv, vv) = GeometryRecipe.OrientPoint(us, vs, (1, 1), 1, false);
            double viewX = (0.1 + 0.8 * uv) * 600, viewY = (0.2 + 0.6 * vv) * 1000;
            var (ex, ey) = GeometryRecipe.UnOrientPoint(viewX, viewY, (1000, 600), 1, false);
            var (x, y) = map.Source(us, vs);
            Assert.Equal(ex / 1000, x, 3);
            Assert.Equal(ey / 600, y, 3);
        }
        Check(0.5, 0.5);
        Check(0, 0);
        Check(1, 0.25);
    }

    /// <summary>Builds a 3 × 2 shading map with half light on the left and full light on the right (R, G, B equal).</summary>
    private static DewarpShading HalfLitLeft(double strength, bool on = true) =>
        new(3, 2, [0.5f, 0.5f, 0.5f, 0.75f, 0.75f, 0.75f, 1, 1, 1, 0.5f, 0.5f, 0.5f, 0.75f, 0.75f, 0.75f, 1, 1, 1], strength, on);

    [Fact]
    public void Shading_IsDividedOut_WhenSwitchedOn()
    {
        using var grey = new MagickImage(new MagickColor("#808080"), 40, 20);   // 50 % on all channels
        var grid = Affine(5, 4, 0, 1, 0, 1, relW: 1, relH: 0.5, aspect: null);

        double Left(MagickImage img) => AiEnhancer.ReadPixels(img, "RGB")[(10 * 40 + 1) * 3] / 65535.0;
        double Right(MagickImage img) => AiEnhancer.ReadPixels(img, "RGB")[(10 * 40 + 38) * 3] / 65535.0;

        // switched on, full strength: left ÷ light (a little above 0.5 at the edge) → almost white, right ÷ 1 → unchanged
        var lightLeft = HalfLitLeft(1).Sample(1.5 / 40, 10.5 / 20).R;
        using (var on = Dewarp.MapFor(new DewarpSpec([], Grid: grid with { Shading = HalfLitLeft(1) }), 40, 20)!.Render(grey))
        {
            Assert.Equal(Math.Min(1, 128 / 255.0 / lightLeft), Left(on), 2);
            Assert.InRange(Right(on), 0.49, 0.52);
        }
        // half strength (bright photo): divisor 0.5 · 0.5 + 0.5 = 0.75
        using (var half = Dewarp.MapFor(new DewarpSpec([], Grid: grid with { Shading = HalfLitLeft(0.5) }), 40, 20)!.Render(grey))
            Assert.Equal(128 / 255.0 / (lightLeft * 0.5 + 0.5), Left(half), 2);
        // switched off: same as without a map, but a different recipe (cache key)
        var offSpec = new DewarpSpec([], Grid: grid with { Shading = HalfLitLeft(1, on: false) });
        Assert.NotEqual(new DewarpSpec([], Grid: grid with { Shading = HalfLitLeft(1) }), offSpec);
        Assert.Null(Dewarp.MapFor(offSpec, 40, 20)!.Shading);
        using var off = Dewarp.MapFor(offSpec, 40, 20)!.Render(grey);
        Assert.InRange(Left(off), 0.49, 0.52);
    }

    [Fact]
    public void Shading_TurnsWithTheGrid()
    {
        // a quarter turn as above: the map covers the result and must be reordered exactly like the grid
        var recipe = new GeometryRecipe(Turns: 1);
        var view = Affine(5, 4, 0.1, 0.8, 0.2, 0.6, relW: 0.8, relH: 1.0, aspect: 1.25) with { Shading = HalfLitLeft(1) };
        var grid = recipe.AiDewarpedInView(view, 1000, 600, 28).Dewarp!.Grid!;
        var turned = grid.Shading!;
        Assert.Equal((2, 3), (turned.Width, turned.Height));
        Assert.True(turned.On);
        foreach (var (us, vs) in new[] { (0.0, 0.0), (1.0, 0.0), (0.5, 0.5), (0.0, 1.0), (1.0, 1.0) })
        {
            var (uv, vv) = GeometryRecipe.OrientPoint(us, vs, (1, 1), 1, false);
            Assert.Equal(view.Shading!.Sample(uv, vv).R, turned.Sample(us, vs).R, 4);
        }
    }
}
