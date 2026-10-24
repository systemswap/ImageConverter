using ImageConverter.Core.Imaging;
using ImageConverter.ViewModels;
using ImageMagick;
using ImageMagick.Drawing;

namespace ImageConverter.Tests;

/// <summary>
/// The image look as a separate step (for "settings first, then AI"): identical to the export chain,
/// and neutral really means no effect — otherwise the look would be applied to the image twice.
/// </summary>
public class LookTests
{
    private static MagickImage Photo()
    {
        var img = new MagickImage(MagickColors.SkyBlue, 320, 240);
        img.Draw(new Drawables().FillColor(MagickColors.Orange).Circle(160, 120, 230, 120));
        return img;
    }

    private static readonly Adjustments Look = new(Brightness: 20, Contrast: 15, Vibrance: 30, Dehaze: 40, Warmth: 10);

    [Fact]
    public void RenderLook_MatchesTheExportChain()
    {
        using var src = Photo();
        var options = new ExportOptions(OutputFormat.Png, 90, false, 320, 240, ResampleFilter.Lanczos3, false,
            Denoise: 0.3, SharpenAmount: 0.8, SharpenSigma: 1.2, Dpi: 72, MetadataMode.KeepAll, ColorMode.KeepProfile, Look);

        using var exported = ImageExporter.Render(src, options);
        using var look = ImageExporter.RenderLook(src, 0.3, 0, Look, 0.8, 1.2);

        Assert.NotSame(src, look);
        Assert.Equal(Pixels.FromMagick(exported), Pixels.FromMagick(look));
    }

    [Fact]
    public void RenderLook_WithoutEffect_IsTheImageItself()
    {
        using var src = Photo();
        Assert.Same(src, ImageExporter.RenderLook(src, 0, 0, Adjustments.None, 0, 1));
        Assert.Same(src, MainViewModel.RenderLook(src, new MainViewModel.Look(0, Adjustments.None, 0, 1, 320)));
    }

    [Fact]
    public void Neutral_HasNoEffect_ButKeepsTheFineSettings()
    {
        var look = new MainViewModel.Look(0.4, Look with { DehazePasses = 7, DehazeCast = 48, Tint = Tint.Color, TintHue = 200 }, 1.1, 1.5, 1000);

        var neutral = look.Neutral();

        Assert.False(look.IsNone);
        Assert.True(neutral.IsNone);
        Assert.Equal(7, neutral.Adjust.DehazePasses);
        Assert.Equal(48, neutral.Adjust.DehazeCast);
        Assert.Equal(200, neutral.Adjust.TintHue);
        Assert.Equal(1.5, neutral.SharpenRadius);
    }

    [Fact]
    public void SharpenRadius_ScalesWithTheImageWidth()
    {
        // Export at 1000 px, look computed on the 4000 px image: radius four times as large, same effect in the export
        var look = new MainViewModel.Look(0, Adjustments.None, 1, 1.5, 1000);
        Assert.Equal(6.0, look.SharpenSigma(4000), 6);
    }
}
