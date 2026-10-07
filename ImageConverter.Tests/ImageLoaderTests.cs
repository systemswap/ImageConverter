using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

public sealed class ImageLoaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ic-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(MagickImage image, string name)
    {
        var path = Path.Combine(_dir, name);
        image.Write(path);
        return path;
    }

    [Theory]
    [InlineData(".jpg")]
    [InlineData(".png")]
    [InlineData(".webp")]
    [InlineData(".tif")]
    [InlineData(".avif")]
    [InlineData(".heic")]
    [InlineData(".jxl")]
    public void ModernAndClassicFormats_AreReadable(string extension)
    {
        Assert.Contains(extension, ImageFormats.ReadableExtensions);
    }

    [Fact]
    public async Task Loads_PixelsAsBgra_WithCorrectSize()
    {
        using var image = new MagickImage(MagickColors.Red, 40, 20);
        var path = Write(image, "rot.png");

        using var loaded = await ImageLoader.LoadAsync(path);
        var bgra = ImageOps.ToBgra(loaded.Image);

        Assert.Equal(40, loaded.Width);
        Assert.Equal(20, loaded.Height);
        // Einfarbige PNGs speichert ImageMagick mit 1 Bit — der Export muss trotzdem 8 Bit liefern
        Assert.Equal(40 * 20 * 4, bgra.Length);
        Assert.Equal([0, 0, 255, 255], bgra[..4]);
        Assert.Equal("PNG", loaded.FormatName);
    }

    [Fact]
    public async Task AppliesExifOrientation_LikePhonePhotos()
    {
        // Querformat-Pixel, EXIF sagt "um 90° gedreht anzeigen" — wie bei Handyfotos.
        // ImageMagick schreibt beim Speichern den EXIF-Wert aus der Orientation-
        // Eigenschaft — deshalb beides setzen, so wie eine Kamera die Datei anlegt.
        using var image = new MagickImage(MagickColors.Blue, 40, 20);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        image.SetProfile(exif);
        image.Orientation = OrientationType.RightTop;
        var path = Write(image, "handy.jpg");

        using var loaded = await ImageLoader.LoadAsync(path);

        Assert.Equal(20, loaded.Width);
        Assert.Equal(40, loaded.Height);
    }

    [Fact]
    public async Task ReadsAvif_WrittenByImageMagick()
    {
        using var image = new MagickImage(MagickColors.Green, 64, 32);
        var path = Write(image, "gruen.avif");

        using var loaded = await ImageLoader.LoadAsync(path);

        Assert.Equal(64, loaded.Width);
        Assert.Equal("AVIF", loaded.FormatName);
    }

    [Fact]
    public async Task ReadsDpi_FromFile_AndFallsBackTo72()
    {
        using var image = new MagickImage(MagickColors.Gray, 10, 10);
        image.Density = new Density(300, DensityUnit.PixelsPerInch);
        var with = Write(image, "300dpi.jpg");

        using var noDpi = new MagickImage(MagickColors.Gray, 10, 10);
        var without = Write(noDpi, "ohne.png");

        using var a = await ImageLoader.LoadAsync(with);
        using var b = await ImageLoader.LoadAsync(without);

        Assert.Equal(300, a.Dpi);
        Assert.Equal(ImageLoader.DefaultDpi, b.Dpi);
    }

    [Fact]
    public async Task KeepsTransparency()
    {
        using var image = new MagickImage(MagickColors.Transparent, 10, 10);
        var path = Write(image, "leer.png");

        using var loaded = await ImageLoader.LoadAsync(path);

        Assert.True(loaded.Image.HasAlpha);
        Assert.Equal(0, ImageOps.ToBgra(loaded.Image)[3]);
    }

    [Fact]
    public async Task Cancellation_StopsLoading()
    {
        using var image = new MagickImage(MagickColors.Red, 10, 10);
        var path = Write(image, "abbruch.png");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageLoader.LoadAsync(path, cts.Token));
    }
}
