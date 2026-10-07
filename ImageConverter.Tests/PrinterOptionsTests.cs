using ImageConverter.Core.Imaging;
using ImageConverter.Core.Printing;
using ImageMagick;

namespace ImageConverter.Tests;

public class PrinterOptionsTests
{
    // ------------------------------------------------------------ lpoptions

    [Fact]
    public void Cups_IppEverywhere_ColorAndDuplex()
    {
        const string output = """
            PageSize/Media Size: *A4 A5 Letter
            sides/2-Sided Printing: *one-sided two-sided-long-edge two-sided-short-edge
            print-color-mode/Color Mode: monochrome *color
            print-quality/Print Quality: 3 *4 5
            """;

        var caps = SystemPrinters.ParseCupsCapabilities(output);

        Assert.True(caps.Duplex);
        Assert.True(caps.Color);
    }

    [Fact]
    public void Cups_PpdDriver_DuplexTumble_RgbAndGray()
    {
        const string output = """
            Duplex/Double-Sided Printing: *None DuplexNoTumble DuplexTumble
            ColorModel/Output Mode: Gray *RGB
            """;

        var caps = SystemPrinters.ParseCupsCapabilities(output);

        Assert.True(caps.Duplex);
        Assert.True(caps.Color);
    }

    [Fact]
    public void Cups_MonoLaser_WithoutDuplex()
    {
        const string output = """
            Duplex/Double-Sided Printing: *None
            ColorModel/Output Mode: *Gray
            """;

        var caps = SystemPrinters.ParseCupsCapabilities(output);

        Assert.False(caps.Duplex);   // ausgrauen
        Assert.False(caps.Color);    // nur Schwarzweiß möglich
    }

    [Fact]
    public void Cups_NoInformation_StaysUnknown()
    {
        var caps = SystemPrinters.ParseCupsCapabilities("PageSize/Media Size: *A4 Letter\n");

        Assert.Null(caps.Duplex);    // wählbar lassen
        Assert.Null(caps.Color);
    }

    // ------------------------------------------------------------ lp

    [Theory]
    [InlineData(DuplexMode.OneSided, false, "sides=one-sided", "print-color-mode=color")]
    [InlineData(DuplexMode.LongEdge, true, "sides=two-sided-long-edge", "print-color-mode=monochrome")]
    [InlineData(DuplexMode.ShortEdge, false, "sides=two-sided-short-edge", "print-color-mode=color")]
    public void CupsArgs_ContainSidesAndColor(DuplexMode duplex, bool gray, string sides, string color)
    {
        var args = SystemPrinters.BuildCupsArgs("/tmp/x.pdf", "Laser", new PrintSettings(2, duplex, gray),
            PaperSize.Common[0], "Test");

        Assert.Contains(sides, args);
        Assert.Contains(color, args);
        Assert.Contains("print-scaling=none", args);
        Assert.Contains("media=A4", args);
        Assert.Equal("2", args[args.IndexOf("-n") + 1]);
        Assert.Equal("/tmp/x.pdf", args[^1]);
    }

    // ------------------------------------------------------------ Graustufen

    [Fact]
    public void Grayscale_RendersNeutralGray()
    {
        using var img = new MagickImage(MagickColors.Orange, 60, 40);
        var options = new ExportOptions(OutputFormat.Jpeg, 90, false, 60, 40, ResampleFilter.Lanczos3, false,
            0, 0, 1, 300, MetadataMode.KeepAll, ColorMode.KeepProfile);
        var p = new Placement(1, 0, 0, 5, 3.4, false, 1);

        using var back = new MagickImage(PrintDocument.RenderForPrint(img, options, p, grayscale: true));
        var px = ImageOps.ToBgra(back);

        // B, G, R gleich (±2 wegen JPEG)
        Assert.InRange(Math.Abs(px[0] - px[1]), 0, 2);
        Assert.InRange(Math.Abs(px[1] - px[2]), 0, 2);
    }
}
