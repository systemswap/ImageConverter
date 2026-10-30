using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>
/// Order of colour space and filters: with "convert to sRGB" the filters compute in sRGB, as the preview does, so that a
/// wide-gamut photo in the file looks as it was set on screen.
/// </summary>
public class ColorOrderTests
{
    private const int W = 200, H = 150;

    /// <summary>Saturated colours with fine detail, in a colour space wider than sRGB.</summary>
    private static MagickImage WideGamut()
    {
        var img = new MagickImage(MagickColors.Black, W, H) { Depth = 16 };
        var data = new ushort[W * H * 3];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 3;
            data[i] = (ushort)(50000 + (x * 7 + y * 3) % 15000);
            data[i + 1] = (ushort)((x * 13 + y * 5) % 12000);
            data[i + 2] = (ushort)(8000 + (x * 3 + y * 11) % 20000);
        }
        using (var pixels = img.GetPixelsUnsafe()) pixels.SetArea(0, 0, W, H, data);
        img.SetProfile(ColorProfiles.AdobeRGB1998);
        return img;
    }

    private static ExportOptions Options(ColorMode color, int w = W, int h = H, double denoise = 0.3, Adjustments? adjust = null) =>
        new(OutputFormat.Png, 90, false, w, h, ResampleFilter.Lanczos3, false, denoise, 0.5, 1, 96, MetadataMode.KeepAll, color,
            adjust ?? new Adjustments(Brightness: 10, Shadows: 20, Dehaze: 40), HighBitDepth: true);

    private static ushort[] Rgb(MagickImage img)
    {
        using var p = img.GetPixelsUnsafe();
        return p.ToShortArray(0, 0, img.Width, img.Height, "RGB")!;
    }

    [Fact]
    public void ConvertToSrgb_FiltersComputeInSrgb_AsThePreviewDoes()
    {
        using var wide = WideGamut();
        using var inSrgb = new MagickImage(wide);
        inSrgb.TransformColorSpace(ColorProfiles.SRGB);

        using var result = ImageExporter.Render(wide, Options(ColorMode.ConvertToSrgb));
        using var expected = ImageExporter.Render(inSrgb, Options(ColorMode.ConvertToSrgb));
        Assert.Equal(Rgb(expected), Rgb(result));

        // The other order really is something else: otherwise this test would prove nothing
        using var filteredFirst = ImageExporter.Render(wide, Options(ColorMode.KeepProfile));
        filteredFirst.TransformColorSpace(ColorProfiles.SRGB);
        Assert.NotEqual(Rgb(filteredFirst), Rgb(result));
    }

    [Fact]
    public void KeepProfile_StaysInTheColourSpaceOfTheSource()
    {
        using var wide = WideGamut();
        using var result = ImageExporter.Render(wide, Options(ColorMode.KeepProfile));
        Assert.False(ImageExporter.IsSrgb(result.GetColorProfile()!));
    }

    [Fact]
    public void SizeEstimateAndExport_AgreeOnWideGamutImages()
    {
        using var wide = WideGamut();
        using var cache = new LookCache();
        using var plain = ImageExporter.Render(wide, Options(ColorMode.ConvertToSrgb, 100, 75));
        using var viaCache = ImageExporter.Render(wide, Options(ColorMode.ConvertToSrgb, 100, 75), null, cache);
        Assert.Equal(Rgb(plain), Rgb(viaCache));

        // A different colour mode must not get the stored result
        using var keep = ImageExporter.Render(wide, Options(ColorMode.KeepProfile, 100, 75), null, cache);
        Assert.NotEqual(Rgb(plain), Rgb(keep));
    }

    [Fact]
    public void Enlarging_DenoisesInSrgb_Too()
    {
        using var wide = WideGamut();
        using var inSrgb = new MagickImage(wide);
        inSrgb.TransformColorSpace(ColorProfiles.SRGB);
        using var result = ImageExporter.Render(wide, Options(ColorMode.ConvertToSrgb, W * 2, H * 2));
        using var expected = ImageExporter.Render(inSrgb, Options(ColorMode.ConvertToSrgb, W * 2, H * 2));
        Assert.Equal(Rgb(expected), Rgb(result));
    }

    [Fact]
    public void WithoutFilters_NothingChanges()
    {
        using var wide = WideGamut();
        var none = new ExportOptions(OutputFormat.Png, 90, false, W, H, ResampleFilter.Lanczos3, false, 0, 0, 1, 96,
            MetadataMode.KeepAll, ColorMode.ConvertToSrgb, HighBitDepth: true);
        using var result = ImageExporter.Render(wide, none);
        using var expected = new MagickImage(wide);
        expected.TransformColorSpace(ColorProfiles.SRGB);
        Assert.Equal(Rgb(expected), Rgb(result));
    }
}
