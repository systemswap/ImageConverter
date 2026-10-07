using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

public class ImageOpsTests
{
    private static byte AlphaAt(MagickImage image, int x, int y)
    {
        var bgra = ImageOps.ToBgra(image);
        return bgra[(y * (int)image.Width + x) * 4 + 3];
    }

    [Fact]
    public void Crop_WithoutRadius_IsPlainRectangle()
    {
        using var src = new MagickImage(MagickColors.Red, 100, 80);
        using var cut = ImageOps.Crop(src, 10, 20, 50, 30, radius: 0);

        Assert.Equal(50u, cut.Width);
        Assert.Equal(30u, cut.Height);
        Assert.Equal(255, AlphaAt(cut, 0, 0));
    }

    [Fact]
    public void Crop_SquareWithMaxRadius_BecomesCircle()
    {
        using var src = new MagickImage(MagickColors.Red, 200, 200);
        using var circle = ImageOps.Crop(src, 0, 0, 100, 100, radius: 50);

        Assert.Equal(100u, circle.Width);                 // bleibt ein Rechteck
        Assert.Equal(0, AlphaAt(circle, 0, 0));           // Ecke transparent
        Assert.Equal(0, AlphaAt(circle, 10, 10));         // außerhalb des Kreises
        Assert.Equal(255, AlphaAt(circle, 50, 1));        // oben mittig: im Kreis
        Assert.Equal(255, AlphaAt(circle, 50, 50));       // Mitte
        Assert.Equal(255, AlphaAt(circle, 25, 25));       // innerhalb (Abstand 35 < 50)
    }

    [Fact]
    public void Crop_SmallRadius_OnlyRoundsCorners()
    {
        using var src = new MagickImage(MagickColors.Red, 200, 200);
        using var rounded = ImageOps.Crop(src, 0, 0, 100, 60, radius: 10);

        Assert.Equal(0, AlphaAt(rounded, 0, 0));
        Assert.Equal(255, AlphaAt(rounded, 10, 10));      // schon innerhalb der Rundung
        Assert.Equal(255, AlphaAt(rounded, 50, 0));       // Kante bleibt gerade
    }

    [Fact]
    public void Rotate_SwapsDimensions_AndFlipKeepsThem()
    {
        using var src = new MagickImage(MagickColors.Red, 40, 20);
        using var rotated = ImageOps.Rotate90(src, clockwise: true);
        using var flipped = ImageOps.FlipHorizontal(src);

        Assert.Equal((20u, 40u), (rotated.Width, rotated.Height));
        Assert.Equal((40u, 20u), (flipped.Width, flipped.Height));
    }

    [Theory]
    [InlineData(ResampleFilter.Lanczos3, true)]
    [InlineData(ResampleFilter.Nearest, false)]
    [InlineData(ResampleFilter.Box, true)]
    public void Resize_HitsExactTargetSize(ResampleFilter filter, bool gamma)
    {
        using var src = new MagickImage(MagickColors.Red, 400, 300);
        using var small = ImageOps.Resize(src, 123, 77, filter, gamma);

        Assert.Equal((123u, 77u), (small.Width, small.Height));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resize_OpaqueImage_StaysOpaque(bool gamma)
    {
        // Wie ein JPEG: kein Alphakanal
        using var src = new MagickImage(MagickColors.SkyBlue, 400, 300);
        src.Format = MagickFormat.Jpeg;
        src.Alpha(AlphaOption.Off);

        using var small = ImageOps.Resize(src, 200, 150, ResampleFilter.Lanczos3, gamma);

        Assert.Equal(255, AlphaAt(small, 100, 75));
    }

    [Fact]
    public async Task Resize_LoadedJpeg_StaysOpaque()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ic-opaque-{Guid.NewGuid():N}.jpg");
        using (var img = new MagickImage(MagickColors.SkyBlue, 400, 300)) img.Write(path);
        try
        {
            using var loaded = await ImageLoader.LoadAsync(path);
            using var small = ImageOps.Resize(loaded.Image, 200, 150, ResampleFilter.Lanczos3, gammaCorrect: true);
            Assert.Equal(255, AlphaAt(small, 100, 75));
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Regression: Anzeige-Export und danach Skalieren (genau die Reihenfolge der App)
    /// lieferte schwarz-transparente Pixel, solange ToBgra über ToByteArray(format) lief.
    /// </summary>
    [Fact]
    public async Task ExportForDisplay_ThenResize_KeepsPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ic-order-{Guid.NewGuid():N}.jpg");
        using (var img = new MagickImage(MagickColors.SkyBlue, 800, 600)) img.Write(path);
        try
        {
            using var loaded = await ImageLoader.LoadAsync(path);
            _ = ImageOps.ToBgra(loaded.Image);   // wie SetDocumentAsync

            using var small = ImageOps.Resize(loaded.Image, 400, 300, ResampleFilter.Lanczos3, gammaCorrect: true);
            var bgra = ImageOps.ToBgra(small);
            var p = (150 * 400 + 200) * 4;

            Assert.Equal(255, bgra[p + 3]);
            Assert.InRange(bgra[p + 2], 100, 160);   // Rotanteil von SkyBlue (135)
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Qualitätswächter: Gamma-korrektes Skalieren darf dunkle Tonstufen nicht
    /// zusammenfallen lassen. Mit Q8 blieben hier nur 6 von 41 übrig (Banding in
    /// Schatten) — deshalb Q16. Fällt der Test, ist vermutlich wieder Q8 im Spiel.
    /// </summary>
    [Fact]
    public void GammaCorrectResize_KeepsDarkTones()
    {
        // 82 × 1: jede Stufe 0–40 doppelt nebeneinander, Box halbiert auf 41 × 1
        const int levels = 41;
        var bgra = new byte[levels * 2 * 4];
        for (var x = 0; x < levels * 2; x++)
        {
            var v = (byte)(x / 2);
            bgra[x * 4] = bgra[x * 4 + 1] = bgra[x * 4 + 2] = v;
            bgra[x * 4 + 3] = 255;
        }
        using var src = ImageOps.FromBgra(bgra, levels * 2, 1);
        using var small = ImageOps.Resize(src, levels, 1, ResampleFilter.Box, gammaCorrect: true);

        var result = ImageOps.ToBgra(small);
        var distinct = Enumerable.Range(0, levels).Select(x => result[x * 4]).Distinct().Count();

        Assert.True(distinct >= 38, $"nur {distinct} von {levels} dunklen Stufen erhalten");
    }

    [Fact]
    public void Bgra_RoundTrip_KeepsPixels()
    {
        using var src = new MagickImage(MagickColors.Orange, 7, 5);
        var bgra = ImageOps.ToBgra(src);
        using var back = ImageOps.FromBgra(bgra, 7, 5);

        Assert.Equal(bgra, ImageOps.ToBgra(back));
    }
}
