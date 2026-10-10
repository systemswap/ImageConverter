using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageMagick.Drawing;

namespace ImageConverter.Tests;

/// <summary>Ausrichten als Rezept: Reihenfolge, Vierteldrehungen, Umrechnen der Punkte, Werkzeug-Ansichten.</summary>
public class GeometryRecipeTests
{
    /// <summary>Unsymmetrisches Testbild: grau mit farbigen Ecken — jede Drehung/Spiegelung sieht anders aus.</summary>
    private static MagickImage Pattern(int w = 120, int h = 80)
    {
        var img = new MagickImage(new MagickColor(30000, 30000, 30000), (uint)w, (uint)h);
        new Drawables().FillColor(MagickColors.Red).Rectangle(0, 0, 19, 9).Draw(img);
        new Drawables().FillColor(MagickColors.Blue).Rectangle(w - 10, h - 30, w - 1, h - 1).Draw(img);
        img.Alpha(AlphaOption.Off);
        return img;
    }

    private static ushort[] Rgb(MagickImage img)
    {
        using var p = img.GetPixelsUnsafe();
        return p.ToShortArray(0, 0, img.Width, img.Height, "RGB")!;
    }

    [Fact]
    public void ThreeRightTurns_AreOne270Turn_AndMatchTurningThePixels()
    {
        var r = GeometryRecipe.None.Rotated(true).Rotated(true).Rotated(true);
        Assert.Equal(3, r.NormalTurns);
        Assert.Equal(GeometryRecipe.None, r.Rotated(true) with { Turns = 0 });   // vier Drehungen: nichts

        using var src = Pattern();
        using var viaRecipe = r.Apply(src)!;
        using var step1 = ImageOps.Rotate90(src, true);
        using var step2 = ImageOps.Rotate90(step1, true);
        using var step3 = ImageOps.Rotate90(step2, true);
        Assert.Equal(Rgb(step3), Rgb(viaRecipe));
    }

    [Fact]
    public void TurnsAndFlips_InAnyOrder_MatchDoingThemOneAfterAnother()
    {
        using var src = Pattern();
        // rechts, spiegeln, rechts, links, spiegeln, rechts — wie nacheinander auf die Pixel
        var recipe = GeometryRecipe.None;
        var pixels = ImageOps.CloneOf(src);
        foreach (var action in new[] { "R", "F", "R", "L", "F", "R" })
        {
            var next = action switch
            {
                "R" => ImageOps.Rotate90(pixels, true),
                "L" => ImageOps.Rotate90(pixels, false),
                _ => ImageOps.FlipHorizontal(pixels),
            };
            pixels.Dispose();
            pixels = next;
            recipe = action switch { "R" => recipe.Rotated(true), "L" => recipe.Rotated(false), _ => recipe.Flipped() };
        }
        using var viaRecipe = recipe.Apply(src) ?? ImageOps.CloneOf(src);
        Assert.Equal(Rgb(pixels), Rgb(viaRecipe));
        pixels.Dispose();
    }

    private static GeometryRecipe Busy() => new(
        Quad: new RelQuad(0.05, 0.08, 0.93, 0.03, 0.97, 0.95, 0.02, 0.9),
        Angle: 7.5,
        Crop: new RelRect(0.2, 0.2, 0.55, 0.5),
        Turns: 1,
        Flip: true);

    [Fact]
    public void Mapping_RoundTrips_ThroughAllStages()
    {
        var r = Busy();
        foreach (var (x, y) in new[] { (40.0, 30.0), (60.0, 45.0), (75.3, 52.9) })
        {
            var (dx, dy) = r.ToResult(x, y, 120, 80);
            var (sx, sy) = r.ToSource(dx, dy, 120, 80);
            Assert.Equal(x, sx, 6);
            Assert.Equal(y, sy, 6);
        }
    }

    [Fact]
    public void Mapping_HitsTheMarkedPixel_InTheRenderedResult()
    {
        using var src = new MagickImage(new MagickColor(20000, 20000, 20000), 300, 200);
        new Drawables().FillColor(MagickColors.Red).Circle(150, 100, 153, 100).Draw(src);   // Punkt bei (150, 100)
        src.Alpha(AlphaOption.Off);
        var r = Busy() with { Crop = new RelRect(0.1, 0.1, 0.7, 0.7) };
        using var result = r.Apply(src)!;
        Assert.Equal(r.SizeFor(300, 200), ((int)result.Width, (int)result.Height));

        // Schwerpunkt der roten Pixel im Ergebnis
        var rgb = Rgb(result);
        double sx = 0, sy = 0, n = 0;
        for (var y = 0; y < result.Height; y++)
        for (var x = 0; x < result.Width; x++)
        {
            var i = (int)((y * result.Width + x) * 3);
            if (rgb[i] > 50000 && rgb[i + 1] < 20000) { sx += x + 0.5; sy += y + 0.5; n++; }
        }
        Assert.True(n > 5, "roter Punkt nicht im Ergebnis");
        var (ex, ey) = r.ToResult(150.5, 100.5, 300, 200);
        Assert.InRange(sx / n - ex, -2.5, 2.5);
        Assert.InRange(sy / n - ey, -2.5, 2.5);
    }

    [Fact]
    public void HidingAStage_KeepsTheOthers()
    {
        var r = Busy();
        Assert.Equal(GeometryStages.All, r.Present);
        var noStraighten = r.PlanFor(120, 80, GeometryStages.All & ~GeometryStages.Straighten);
        Assert.Null(noStraighten.QuadPixels);
        Assert.Equal(1, noStraighten.Turns);
        Assert.NotNull(noStraighten.Rect);                   // Zuschnitt bleibt (relativ zum Bild ohne Entzerren)
        var noOrient = r.SizeFor(120, 80, GeometryStages.All & ~GeometryStages.Orient);
        var all = r.SizeFor(120, 80);
        Assert.Equal((noOrient.Height, noOrient.Width), all); // nur die Vierteldrehung fehlt
    }

    [Fact]
    public void LevelWithoutCrop_UsesLargestRectangleInside_NoEmptyCorners()
    {
        using var src = Pattern(200, 100);
        var r = new GeometryRecipe(Angle: 10);
        using var result = r.Apply(src)!;
        using var px = result.GetPixelsUnsafe();
        // Ecken des Ergebnisses sind Bild, nicht leer (keine Transparenz, kein Schwarz)
        foreach (var (x, y) in new[] { (0, 0), ((int)result.Width - 1, 0), (0, (int)result.Height - 1), ((int)result.Width - 1, (int)result.Height - 1) })
        {
            var c = px.GetPixel(x, y).ToColor()!;
            Assert.True(c.A > 65000 && c.R + c.G + c.B > 20000, $"leere Ecke bei {x},{y}: {c}");
        }
        Assert.True(result.Width < 200 && result.Height < 100);   // kleiner als das Bild: der Rahmen liegt innen
    }

    [Fact]
    public void CropInResult_OnATurnedImage_CutsTheShownRegion()
    {
        using var src = Pattern(120, 80);
        var r = GeometryRecipe.None.Rotated(true);           // angezeigt hochkant 80 × 120
        // oben rechts im gedrehten Bild liegt die rote Ecke (vorher oben links)
        var cropped = r.CroppedInResult(50, 0, 30, 30, 0, 120, 80, GeometryStages.All);
        using var result = cropped.Apply(src)!;
        Assert.Equal((30, 30), ((int)result.Width, (int)result.Height));
        using var px = result.GetPixelsUnsafe();
        var corner = px.GetPixel(25, 5).ToColor()!;          // rote Ecke rechts oben im Ausschnitt
        Assert.True(corner.R > 60000 && corner.G < 5000, $"{corner}");

        // Zweiter Zuschnitt schneidet enger, im ersten
        var again = cropped.CroppedInResult(10, 0, 20, 10, 0, 120, 80, GeometryStages.All);
        Assert.Equal((20, 10), again.SizeFor(120, 80));
    }

    [Fact]
    public void LevelView_RoundTrips_WithTurnsAndFlip()
    {
        var r = new GeometryRecipe(Angle: -4, Crop: new RelRect(0.1, 0.15, 0.6, 0.5), Turns: 3, Flip: true);
        var (angle, frame) = r.LevelInView(120, 80);
        var back = r.LevelledInView(angle, frame!.Value.X, frame.Value.Y, frame.Value.W, frame.Value.H, 0, 120, 80);
        Assert.Equal(r.Angle, back.Angle, 6);
        Assert.Equal(r.Crop!.Value.X, back.Crop!.Value.X, 6);
        Assert.Equal(r.Crop.Value.Y, back.Crop.Value.Y, 6);
        Assert.Equal(r.Crop.Value.Width, back.Crop.Value.Width, 6);
        Assert.Equal(r.Crop.Value.Height, back.Crop.Value.Height, 6);
    }

    [Fact]
    public void StraightenView_RoundTrips_AndKeepsCornerOrder_WhenFlipped()
    {
        var r = new GeometryRecipe(Quad: new RelQuad(0.1, 0.1, 0.9, 0.05, 0.95, 0.9, 0.05, 0.95), Turns: 1, Flip: true);
        var view = r.QuadInView(120, 80)!;
        var back = r.StraightenedInView(view, 120, 80);
        Assert.Equal(r.Quad, back.Quad);
        Assert.True(PerspectiveGeometry.IsValid(back.Quad!.Value.ToPixels(120, 80)));
    }

    [Fact]
    public void None_DoesNothing()
    {
        using var src = Pattern();
        Assert.Null(GeometryRecipe.None.Apply(src));
        Assert.True(GeometryRecipe.None.IsNone);
        Assert.True(GeometryRecipe.None.Rotated(false).Rotated(true).IsNone);
        Assert.Equal((120, 80), GeometryRecipe.None.SizeFor(120, 80));
    }
}
