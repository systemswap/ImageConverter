using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.Tests;

/// <summary>Tests header probing for lists: same size and DPI as a full load, thumbnail the right way up.</summary>
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
        Assert.Equal(full.Width > full.Height, thumb.Width > thumb.Height);   // the right way up
    }

    /// <summary>Writes a PNG consisting only of a header and empty content that claims arbitrary dimensions (like a crafted file).</summary>
    private string WriteClaimingPng(int width, int height)
    {
        static byte[] Chunk(string type, byte[] data)
        {
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            var crcInput = t.Concat(data).ToArray();
            uint crc = 0xFFFFFFFF;
            foreach (var b in crcInput) { crc ^= b; for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1)); }
            return BigEndian(data.Length).Concat(t).Concat(data).Concat(BigEndian((int)~crc)).ToArray();
        }
        static byte[] BigEndian(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
        var ihdr = BigEndian(width).Concat(BigEndian(height)).Concat(new byte[] { 8, 0, 0, 0, 0 }).ToArray();
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Concat(Chunk("IHDR", ihdr)).Concat(Chunk("IDAT", [120, 156, 3, 0, 0, 0, 0, 1]))
            .Concat(Chunk("IEND", [])).ToArray();
        var path = Path.Combine(_dir, $"behauptet-{width}x{height}.png");
        File.WriteAllBytes(path, png);
        return path;
    }

    [Fact]
    public async Task HugeClaimedSize_IsRejected_BeforeDecoding()
    {
        // 60000 × 60000 = 3.6 gigapixels from a few bytes; without the check, ImageMagick would allocate memory of this size
        var path = WriteClaimingPng(60000, 60000);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<ImageTooLargeException>(() => ImageLoader.LoadAsync(path));
        Assert.Contains("60000", e.Message);
        await Assert.ThrowsAsync<ImageTooLargeException>(() => ImageLoader.LoadThumbnailAsync(path, 64));
        Assert.True(clock.Elapsed.TotalSeconds < 2, $"{clock.Elapsed}");
        Assert.InRange(ImageLoader.MaxPixels, 100_000_000, 2_000_000_000);
    }

    /// <summary>A file whose content does not match its extension (an AVIF or JPEG saved as .png) opens by its content
    /// (Fehlergedächtnis #126).</summary>
    [Theory]
    [InlineData("falsch.png", MagickFormat.Jpeg, "JPEG")]
    [InlineData("falsch.jpg", MagickFormat.WebP, "WEBP")]
    [InlineData("falsch.webp", MagickFormat.Png, "PNG")]
    public async Task ContentNotMatchingExtension_LoadsByContent(string name, MagickFormat content, string expected)
    {
        var path = Path.Combine(_dir, name);
        using (var img = new MagickImage(new MagickColor(20000, 30000, 40000), 300, 200))
            img.Write(path, content);

        using var loaded = await ImageLoader.LoadAsync(path);
        Assert.Equal((300, 200, expected), (loaded.Width, loaded.Height, loaded.FormatName));
    }
}
