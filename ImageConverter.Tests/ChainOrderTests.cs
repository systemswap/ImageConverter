using ImageConverter.Core.Ai;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>
/// Tests the processing order that gives the best quality: white balance („Gleiche Farbe“) before the contrast curve;
/// when enlarging, denoise first, then enlarge.
/// </summary>
public class ChainOrderTests
{
    [Fact]
    public void Neutralize_BeforeContrast_LeavesGreyNeutral()
    {
        // Grey with a reddish-yellow colour cast, plus strong contrast
        const int w = 64, h = 48;
        var px = new float[w * h * 4];
        for (var i = 0; i < w * h; i++) (px[i * 4], px[i * 4 + 1], px[i * 4 + 2], px[i * 4 + 3]) = (0.40f, 0.48f, 0.58f, 1);

        var result = Adjust.Apply(px, w, h, new Adjustments(Contrast: 60, Neutralize: 100));

        // The cast is removed before the contrast can spread it: R, G, B almost equal.
        var (b, g, r) = (result[0], result[1], result[2]);
        Assert.True(Math.Abs(r - b) < 0.02 && Math.Abs(r - g) < 0.02, $"R {r:0.000}, G {g:0.000}, B {b:0.000}");
    }

    [Fact]
    public void Enlarging_DenoisesFirst_ThenEnlarges()
    {
        const int w = 120, h = 90;
        var noise = FaceDetails.GrainField(0, 0, w, h, 0, seed: 5);
        var data = new ushort[w * h * 3];
        for (var i = 0; i < w * h; i++)
        {
            var v = (ushort)(Math.Clamp(0.5f + 0.04f * noise[i], 0, 1) * 65535);
            data[i * 3] = data[i * 3 + 1] = data[i * 3 + 2] = v;
        }
        using var source = new MagickImage();
        source.ReadPixels(data, new PixelReadSettings(w, h, StorageType.Quantum, "RGB"));

        var options = new ExportOptions(OutputFormat.Png, 90, false, w * 2, h * 2, ResampleFilter.Lanczos3, false,
            Denoise: 0.7, SharpenAmount: 0, SharpenSigma: 1, Dpi: 72, MetadataMode.KeepAll, ColorMode.KeepProfile);
        Assert.True(ImageExporter.NoiseFirst(source, options));

        using var exported = ImageExporter.Render(source, options);
        using var clean = ImageExporter.RenderLook(source, 0.7, 0, null, 0, 1);
        using var expected = ImageOps.Resize(clean, w * 2, h * 2, ResampleFilter.Lanczos3, false);

        Assert.Equal(Pixels.FromMagick(expected), Pixels.FromMagick(exported));
    }
}
