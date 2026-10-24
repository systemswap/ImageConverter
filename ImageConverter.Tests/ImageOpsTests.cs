using ImageConverter.Core.Imaging;
using ImageMagick;
using ImageMagick.Drawing;

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

        Assert.Equal(100u, circle.Width);                 // stays a rectangle
        Assert.Equal(0, AlphaAt(circle, 0, 0));           // corner transparent
        Assert.Equal(0, AlphaAt(circle, 10, 10));         // outside the circle
        Assert.Equal(255, AlphaAt(circle, 50, 1));        // top centre: inside the circle
        Assert.Equal(255, AlphaAt(circle, 50, 50));       // centre
        Assert.Equal(255, AlphaAt(circle, 25, 25));       // inside (distance 35 < 50)
    }

    [Fact]
    public void Crop_SmallRadius_OnlyRoundsCorners()
    {
        using var src = new MagickImage(MagickColors.Red, 200, 200);
        using var rounded = ImageOps.Crop(src, 0, 0, 100, 60, radius: 10);

        Assert.Equal(0, AlphaAt(rounded, 0, 0));
        Assert.Equal(255, AlphaAt(rounded, 10, 10));      // already inside the rounding
        Assert.Equal(255, AlphaAt(rounded, 50, 0));       // edge stays straight
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
        // Like a JPEG: no alpha channel
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
    /// Regression test: exporting for display and then resizing (exactly the order used by the app)
    /// produces black, transparent pixels if ToBgra goes through ToByteArray(format).
    /// </summary>
    [Fact]
    public async Task ExportForDisplay_ThenResize_KeepsPixels()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ic-order-{Guid.NewGuid():N}.jpg");
        using (var img = new MagickImage(MagickColors.SkyBlue, 800, 600)) img.Write(path);
        try
        {
            using var loaded = await ImageLoader.LoadAsync(path);
            _ = ImageOps.ToBgra(loaded.Image);   // as in SetDocumentAsync

            using var small = ImageOps.Resize(loaded.Image, 400, 300, ResampleFilter.Lanczos3, gammaCorrect: true);
            var bgra = ImageOps.ToBgra(small);
            var p = (150 * 400 + 200) * 4;

            Assert.Equal(255, bgra[p + 3]);
            Assert.InRange(bgra[p + 2], 100, 160);   // red component of SkyBlue (135)
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Quality guard: gamma-correct resizing must not merge dark tone levels. With a Q8 build
    /// of ImageMagick only 6 of 41 levels remain here (banding in shadows), which is why Q16 is
    /// used. If this test fails, a Q8 build is probably in use.
    /// </summary>
    [Fact]
    public void GammaCorrectResize_KeepsDarkTones()
    {
        // 82 × 1: each level 0–40 twice side by side, box filter halves to 41 × 1
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

    /// <summary>
    /// Large images are resized in parallel strips (otherwise Magick.NET computes on a single
    /// core). The result must match the plain ImageMagick path, including transparency, colour
    /// profile, EXIF and 8-bit depth.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resize_LargeImage_InStrips_MatchesImageMagick(bool gamma)
    {
        using var src = new MagickImage("plasma:fractal", 1600, 1000);
        Assert.True(src.Width * src.Height >= ImageOps.ParallelMinPixels);
        src.Alpha(AlphaOption.Set);
        src.Draw(new Drawables().FillColor(MagickColors.Transparent).Rectangle(100, 100, 700, 500));
        src.Depth = 8;
        src.SetProfile(ColorProfiles.AdobeRGB1998);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Make, "Testkamera");
        src.SetProfile(exif);

        using var strips = ImageOps.Resize(src, 640, 400, ResampleFilter.Lanczos3, gamma);

        using var plain = (MagickImage)src.Clone();
        if (gamma) plain.ColorSpace = ColorSpace.RGB;
        plain.FilterType = FilterType.Lanczos;
        plain.Resize(new MagickGeometry(640, 400) { IgnoreAspectRatio = true });
        if (gamma) plain.ColorSpace = ColorSpace.sRGB;

        var a = plain.GetPixelsUnsafe().ToShortArray("RGBA")!;
        var b = strips.GetPixelsUnsafe().ToShortArray("RGBA")!;
        var diffs = a.Zip(b, (x, y) => Math.Abs(x - y) / 257.0).ToArray();

        // Rounding of intermediate values: single pixels up to 3 levels (of 255), practically 0 on average
        // (the test image is a random pattern, so the largest deviation varies between 1 and 2.5)
        Assert.Equal((640u, 400u), (strips.Width, strips.Height));
        Assert.True(diffs.Max() <= 3, $"Abweichung bis {diffs.Max():F2} Stufen");
        Assert.True(diffs.Average() < 0.05, $"mittlere Abweichung {diffs.Average():F3} Stufen");
        Assert.True(strips.HasAlpha);
        Assert.Equal(8u, strips.Depth);
        Assert.Contains("Adobe", strips.GetColorProfile()?.Description ?? "");
        Assert.Equal("Testkamera", strips.GetExifProfile()?.GetValue(ExifTag.Make)?.Value);
    }

    /// <summary>
    /// Preview, size estimation and export read the same image at the same time. Reproduced
    /// here with many parallel tasks; none may fail or return a wrong result.
    /// </summary>
    [Fact]
    public async Task ConcurrentOperations_OnSameSource_AreSafe()
    {
        // Above ParallelMinPixels, so resizing in strips also runs concurrently here
        using var src = new MagickImage(MagickColors.SkyBlue, 1500, 800);
        src.SetProfile(ColorProfiles.AdobeRGB1998);
        var expectedDisplay = ImageOps.ToDisplayBgra(src);

        var tasks = Enumerable.Range(0, 48).Select(i => Task.Run(() =>
        {
            switch (i % 4)
            {
                case 0:
                    using (var r = ImageOps.Resize(src, 300, 200, ResampleFilter.Lanczos3, gammaCorrect: true))
                        Assert.Contains("Adobe", r.GetColorProfile()?.Description ?? "");
                    break;
                case 1:
                    using (var r = ImageOps.Rotate90(src, clockwise: true))
                        Assert.Equal(800u, r.Width);
                    break;
                case 2:
                    Assert.Equal(expectedDisplay, ImageOps.ToDisplayBgra(src));
                    break;
                default:
                    using (var c = ImageOps.Crop(src, 10, 10, 200, 200, radius: 50))
                        Assert.Equal(200u, c.Width);
                    break;
            }
        }));

        await Task.WhenAll(tasks);

        // The source itself is unchanged.
        Assert.Equal((1500u, 800u), (src.Width, src.Height));
        Assert.Contains("Adobe", src.GetColorProfile()?.Description ?? "");
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
