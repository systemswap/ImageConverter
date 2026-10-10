using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>
/// Frei drehen: der automatische Rahmen (größtes Rechteck im Seitenverhältnis, ganz im gedrehten
/// Bild) und das Drehen + Zuschneiden des echten Bildes.
/// </summary>
public class RotationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    [InlineData(-180)]
    public void LargestInside_Straight_IsWholeImage(double degrees)
    {
        var (x, y, w, h) = RotationGeometry.LargestInside(300, 200, degrees, 1.5);
        Assert.Equal(0, x, 6);
        Assert.Equal(0, y, 6);
        Assert.Equal(300, w, 6);
        Assert.Equal(200, h, 6);
    }

    [Fact]
    public void LargestInside_LandscapeAt90_IsSmallLandscape()
    {
        // 300 × 200 um 90° gedreht ist 200 breit und 300 hoch: hinein passt 3:2 nur mit 200 × 133
        var (x, y, w, h) = RotationGeometry.LargestInside(300, 200, 90, 1.5);
        Assert.Equal(200, w, 3);
        Assert.Equal(133.333, h, 3);
        Assert.Equal(50, x, 3);
        Assert.Equal(33.333, y, 3);
    }

    /// <summary>
    /// Erst kleiner, dann wieder größer: Bis 45° schrumpft der Rahmen, danach wächst er wieder —
    /// und er liegt immer genau an den schrägen Kanten an.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7.5)]
    [InlineData(-23)]
    [InlineData(45)]
    [InlineData(67)]
    [InlineData(135)]
    [InlineData(-170)]
    public void LargestInside_FitsExactly(double degrees)
    {
        var (x, y, w, h) = RotationGeometry.LargestInside(300, 200, degrees, 1.5);
        Assert.True(RotationGeometry.ContainsRect(300, 200, degrees, x, y, w, h, tolerance: 1e-6));

        // 1 % größer passt nicht mehr: Der Rahmen nutzt den Platz ganz aus
        var bigger = (w * 1.01, h * 1.01);
        Assert.False(RotationGeometry.ContainsRect(300, 200, degrees,
            150 - bigger.Item1 / 2, 100 - bigger.Item2 / 2, bigger.Item1, bigger.Item2));
        Assert.Equal(1.5, w / h, 6);   // Seitenverhältnis bleibt
    }

    [Fact]
    public void LargestInside_ShrinksThenGrowsAgain()
    {
        double Width(double deg) => RotationGeometry.LargestInside(300, 200, deg, 1.5).Width;
        Assert.True(Width(10) < Width(0));
        Assert.True(Width(30) < Width(10));
        Assert.True(Width(170) > Width(150));
        Assert.Equal(Width(0), Width(180), 6);
    }

    [Fact]
    public void ShrinkInside_KeepsCenterAndEndsInside()
    {
        var (x, y, w, h) = RotationGeometry.ShrinkInside(300, 200, 20, 0, 0, 300, 200);
        Assert.True(RotationGeometry.ContainsRect(300, 200, 20, x, y, w, h, tolerance: 1e-3));
        Assert.Equal(150, x + w / 2, 3);
        Assert.Equal(100, y + h / 2, 3);
    }

    [Theory]
    [InlineData(190, -170)]
    [InlineData(-190, 170)]
    [InlineData(360, 0)]
    [InlineData(45, 45)]
    public void Normalize_StaysWithinHalfCircle(double input, double expected) =>
        Assert.Equal(expected, RotationGeometry.Normalize(input), 6);

    private static (byte B, byte G, byte R, byte A) Pixel(MagickImage image, int x, int y)
    {
        var bgra = ImageOps.ToBgra(image);
        var o = (y * (int)image.Width + x) * 4;
        return (bgra[o], bgra[o + 1], bgra[o + 2], bgra[o + 3]);
    }

    /// <summary>Mit dem automatischen Rahmen bleiben keine leeren Ecken — bis in die Eckpixel nur Bild.</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(-12.5)]
    [InlineData(33)]
    public void RotateAndCrop_LargestFrame_HasNoEmptyCorners(double degrees)
    {
        using var src = new MagickImage(MagickColors.Red, 300, 200);
        var (fx, fy, fw, fh) = RotationGeometry.LargestInside(300, 200, degrees, 1.5);
        // Wie die Vorschau: auf ganze Pixel nach innen gerundet, 1 px Abstand zur Kante
        var x = (int)Math.Ceiling(fx) + 1;
        var y = (int)Math.Ceiling(fy) + 1;
        var w = (int)Math.Floor(fx + fw) - 1 - x;
        var h = (int)Math.Floor(fy + fh) - 1 - y;

        using var result = ImageOps.RotateAndCrop(src, degrees, x, y, w, h, radius: 0);

        Assert.Equal(((uint)w, (uint)h), (result.Width, result.Height));
        Assert.False(result.HasAlpha);   // ohne Transparenz geladen → ohne Alphakanal zurück
        foreach (var (px, py) in new[] { (0, 0), (w - 1, 0), (0, h - 1), (w - 1, h - 1) })
        {
            var (b, g, r, _) = Pixel(result, px, py);
            Assert.True(r > 250 && g < 5 && b < 5, $"Ecke ({px},{py}) ist nicht rot: {r},{g},{b}");
        }
    }

    /// <summary>Gegenprobe zum Test oben: Ein zu großer Rahmen hat leere Ecken — der Test sieht das.</summary>
    [Fact]
    public void RotateAndCrop_TooLargeFrame_HasEmptyCorners()
    {
        using var src = new MagickImage(MagickColors.Red, 300, 200);
        using var result = ImageOps.RotateAndCrop(src, 20, 0, 0, 300, 200, radius: 0);

        var (_, _, r, _) = Pixel(result, 0, 0);
        Assert.True(r < 128, "Die Ecke außerhalb des gedrehten Bildes müsste leer sein.");
    }

    /// <summary>
    /// Positive Winkel drehen im Uhrzeigersinn — wie die Vorschau (Avalonia). Links rot, rechts blau:
    /// um 90° im Uhrzeigersinn liegt Rot oben, gegen den Uhrzeigersinn unten.
    /// </summary>
    [Theory]
    [InlineData(90, true)]
    [InlineData(-90, false)]
    public void RotateAndCrop_PositiveIsClockwise(double degrees, bool redOnTop)
    {
        using var src = new MagickImage(MagickColors.Blue, 200, 100);
        using (var red = new MagickImage(MagickColors.Red, 100, 100))
            src.Composite(red, 0, 0, CompositeOperator.Over);

        var (fx, fy, fw, fh) = RotationGeometry.LargestInside(200, 100, degrees, 2);
        using var result = ImageOps.RotateAndCrop(src, degrees, (int)Math.Round(fx), (int)Math.Round(fy),
            (int)Math.Round(fw), (int)Math.Round(fh), radius: 0);

        var top = Pixel(result, (int)result.Width / 2, 2);
        var bottom = Pixel(result, (int)result.Width / 2, (int)result.Height - 3);
        Assert.Equal(redOnTop, top.R > 200 && top.B < 50);
        Assert.Equal(!redOnTop, bottom.R > 200 && bottom.B < 50);
    }

    [Fact]
    public void RotateAndCrop_ZeroDegrees_IsPlainCrop()
    {
        using var src = new MagickImage(MagickColors.Red, 100, 80);
        using var result = ImageOps.RotateAndCrop(src, 0, 10, 20, 50, 30, radius: 0);
        Assert.Equal((50u, 30u), (result.Width, result.Height));
    }
}
