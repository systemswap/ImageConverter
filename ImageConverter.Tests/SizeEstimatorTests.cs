using ImageConverter.Core.Imaging;
using ImageMagick;
using Xunit.Abstractions;

namespace ImageConverter.Tests;

public class SizeEstimatorTests(ITestOutputHelper output)
{
    private static ExportOptions Options(OutputFormat format, int w, int h) => new(
        format, Quality: 82, Lossless: false, w, h, ResampleFilter.Lanczos3, GammaCorrect: true,
        Denoise: 0, SharpenAmount: 0, SharpenSigma: 1, Dpi: 72,
        MetadataMode.KeepAll, ColorMode.ConvertToSrgb);

    private static MagickImage Smooth()
    {
        // Smooth, large-area gradient — like the test image, compresses extremely well
        var img = new MagickImage("gradient:#4a6fa8-#e8b894", 2000, 1333);
        return img;
    }

    private static MagickImage PhotoLike()
    {
        // Plasma fractal: irregular structures as in a photo
        var img = new MagickImage("plasma:", 2000, 1333);
        img.GaussianBlur(0, 1.2);
        return img;
    }

    private long Actual(MagickImage src, ExportOptions o)
    {
        using var rendered = ImageExporter.Render(src, o);
        using var ms = new MemoryStream();
        ImageExporter.Encode(rendered, o, ms);
        return ms.Length;
    }

    [Theory]
    [InlineData("glatt", OutputFormat.WebP)]
    [InlineData("glatt", OutputFormat.Jpeg)]
    [InlineData("foto", OutputFormat.WebP)]
    [InlineData("foto", OutputFormat.Jpeg)]
    [InlineData("foto", OutputFormat.Avif)]
    public void Estimate_IsWithin35Percent(string kind, OutputFormat format)
    {
        using var src = kind == "glatt" ? Smooth() : PhotoLike();
        var o = Options(format, 1600, 1067);

        var estimate = SizeEstimator.EstimateSize(src, o);
        var actual = Actual(src, o);
        var error = (double)estimate.Bytes / actual - 1;

        output.WriteLine($"{kind} {format}: Schätzung {estimate.Bytes / 1024} KB, tatsächlich {actual / 1024} KB, Abweichung {error:+0%;-0%}");
        Assert.False(estimate.IsExact);
        Assert.InRange(error, -0.35, 0.35);
    }

    [Fact]
    public void SmallTarget_IsExact()
    {
        using var src = PhotoLike();
        var o = Options(OutputFormat.WebP, 800, 533);

        var estimate = SizeEstimator.EstimateSize(src, o);

        Assert.True(estimate.IsExact);
        Assert.Equal(Actual(src, o), estimate.Bytes);
    }
}
