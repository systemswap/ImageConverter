using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Kopfdaten für Listen: gleiche Größe und DPI wie das ganze Laden, Vorschaubild richtig herum.</summary>
public class ImageLoaderProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ic-probe-" + Guid.NewGuid().ToString("N"));

    public ImageLoaderProbeTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(string name, Action<MagickImage> setup)
    {
        using var img = new MagickImage(new MagickColor(20000, 30000, 40000), 300, 200);
        setup(img);
        var path = Path.Combine(_dir, name);
        img.Write(path);
        return path;
    }

    [Theory]
    [InlineData("quer.jpg", OrientationType.TopLeft, 300.0, DensityUnit.PixelsPerInch)]
    [InlineData("gedreht.jpg", OrientationType.RightTop, 240.0, DensityUnit.PixelsPerInch)]
    [InlineData("zentimeter.png", OrientationType.TopLeft, 118.0, DensityUnit.PixelsPerCentimeter)]
    [InlineData("ohne-einheit.tif", OrientationType.LeftBottom, 150.0, DensityUnit.Undefined)]
    public async Task Probe_MatchesFullLoad(string name, OrientationType orientation, double density, DensityUnit unit)
    {
        var path = Write(name, img =>
        {
            img.Orientation = orientation;
            img.Density = new Density(density, density, unit);
        });
        using var full = await ImageLoader.LoadAsync(path);
        var probe = await ImageLoader.ProbeAsync(path);
        Assert.NotNull(probe);
        Assert.Equal((full.Width, full.Height, full.Dpi), (probe.Value.Width, probe.Value.Height, probe.Value.Dpi));

        using var thumb = await ImageLoader.LoadThumbnailAsync(path, 64);
        Assert.True(thumb.Width <= 64 && thumb.Height <= 64);
        Assert.Equal(full.Width > full.Height, thumb.Width > thumb.Height);   // richtig herum
    }
}
