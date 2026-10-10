using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageMagick.Drawing;

namespace ImageConverter.Tests;

/// <summary>Entzerren: Viereck auf etwas Rechteckigem → gerades Rechteck.</summary>
public class PerspectiveTests
{
    // Schräg fotografiertes „Blatt“: oben schmaler als unten
    private static readonly (double X, double Y)[] Trapezoid = [(150, 60), (330, 80), (370, 260), (90, 240)];

    [Fact]
    public void IsValid_AcceptsTrapezoid_RejectsCrossedMirroredConcaveAndTiny()
    {
        Assert.True(PerspectiveGeometry.IsValid(Trapezoid));
        Assert.False(PerspectiveGeometry.IsValid([(150, 60), (370, 260), (330, 80), (90, 240)]));   // überkreuzt
        Assert.False(PerspectiveGeometry.IsValid([(330, 80), (150, 60), (90, 240), (370, 260)]));   // gespiegelt
        Assert.False(PerspectiveGeometry.IsValid([(0, 0), (100, 0), (40, 30), (0, 100)]));          // eingedellt
        Assert.False(PerspectiveGeometry.IsValid([(0, 0), (2, 0), (2, 2), (0, 2)]));                // zu klein
    }

    [Fact]
    public void TargetSize_AveragesOppositeEdges()
    {
        var (w, h) = PerspectiveGeometry.TargetSize([(50, 0), (150, 0), (200, 100), (0, 100)]);
        Assert.Equal(150, w);                                   // (100 + 200) / 2
        Assert.Equal((int)Math.Round(Math.Sqrt(50 * 50 + 100 * 100)), h);
    }

    [Fact]
    public void Homography_MapsCornersExactly()
    {
        (double X, double Y)[] rect = [(0, 0), (200, 0), (200, 150), (0, 150)];
        var h = PerspectiveGeometry.Homography(Trapezoid, rect);
        for (var i = 0; i < 4; i++)
        {
            var (x, y) = PerspectiveGeometry.Apply(h, Trapezoid[i].X, Trapezoid[i].Y);
            Assert.Equal(rect[i].X, x, 6);
            Assert.Equal(rect[i].Y, y, 6);
        }
        Assert.Equal(Trapezoid[2].X, PerspectiveGeometry.PointAt(Trapezoid, 1, 1).X, 6);
    }

    private static MagickImage SkewedSheet()
    {
        var img = new MagickImage(MagickColors.Blue, 420, 300);
        new Drawables().FillColor(MagickColors.Red)
            .Polygon(Trapezoid.Select(p => new PointD(p.X, p.Y)))
            .Draw(img);
        // Grüne Markierung in der Ecke oben links des Blatts
        var mark = PerspectiveGeometry.PointAt(Trapezoid, 0.12, 0.15);
        new Drawables().FillColor(MagickColors.Lime).Circle(mark.X, mark.Y, mark.X + 9, mark.Y).Draw(img);
        img.Alpha(AlphaOption.Off);   // Zeichnen legt einen Alphakanal an — wie ein Foto ohne Transparenz
        return img;
    }

    private static (byte B, byte G, byte R) Pixel(MagickImage image, double fx, double fy)
    {
        var bgra = ImageOps.ToBgra(image);
        var x = (int)(fx * (image.Width - 1));
        var y = (int)(fy * (image.Height - 1));
        var o = (y * (int)image.Width + x) * 4;
        return (bgra[o], bgra[o + 1], bgra[o + 2]);
    }

    [Fact]
    public void Straighten_SheetFillsResult_NotMirroredOrTurned()
    {
        using var src = SkewedSheet();
        using var result = ImageOps.Straighten(src, Trapezoid);

        var (w, h) = PerspectiveGeometry.TargetSize(Trapezoid);
        Assert.Equal(((uint)w, (uint)h), (result.Width, result.Height));
        Assert.False(result.HasAlpha);

        // Ecken (knapp innen) und Mitte: Blatt (rot), kein Hintergrund (blau)
        foreach (var (fx, fy) in new[] { (0.03, 0.03), (0.97, 0.03), (0.03, 0.97), (0.97, 0.97), (0.5, 0.5) })
        {
            var (b, _, r) = Pixel(result, fx, fy);
            Assert.True(r > 200 && b < 60, $"Stelle ({fx},{fy}) ist nicht das Blatt: R{r} B{b}");
        }

        // Markierung oben links, nicht oben rechts oder unten
        Assert.True(Pixel(result, 0.12, 0.15).G > 200, "Markierung fehlt oben links");
        Assert.True(Pixel(result, 0.88, 0.15).G < 100);
        Assert.True(Pixel(result, 0.12, 0.85).G < 100);
    }

    /// <summary>Gegenprobe: Ein zu großes Viereck nimmt Hintergrund mit — der Test oben würde das merken.</summary>
    [Fact]
    public void Straighten_TooLargeQuad_ContainsBackground()
    {
        using var src = SkewedSheet();
        using var result = ImageOps.Straighten(src, [(120, 30), (360, 50), (400, 290), (60, 270)]);
        var (b, _, r) = Pixel(result, 0.02, 0.02);
        Assert.True(b > 200 && r < 60, "Die Ecke müsste Hintergrund (blau) sein.");
    }

    [Fact]
    public void Straighten_InvalidQuad_Throws()
    {
        using var src = new MagickImage(MagickColors.Red, 100, 100);
        Assert.Throws<ArgumentException>(() => ImageOps.Straighten(src, [(0, 0), (90, 90), (90, 0), (0, 90)]));
    }
}
