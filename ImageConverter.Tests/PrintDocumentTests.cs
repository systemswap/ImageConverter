using ImageConverter.Core.Imaging;
using ImageConverter.Core.Printing;
using ImageMagick;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ImageConverter.Tests;

public class PrintDocumentTests
{
    private static ExportOptions Opt(int w, int h, int dpi = 300) => new(
        OutputFormat.Jpeg, 90, false, w, h, ResampleFilter.Lanczos3, GammaCorrect: false,
        Denoise: 0, SharpenAmount: 0, SharpenSigma: 1, Dpi: dpi, MetadataMode.KeepAll, ColorMode.KeepProfile);

    private static int ImagesOn(PdfPage page) =>
        page.Elements.GetDictionary("/Resources")?.Elements.GetDictionary("/XObject")?.Elements.Count ?? 0;

    [Fact]
    public void PrintSize_IsPixelsDividedByDpi()
    {
        var item = PrintDocument.ToItem(1, Opt(1181, 1772, dpi: 300));   // 10 × 15 cm at 300 DPI

        Assert.Equal(100, item.WidthMm, 0);
        Assert.Equal(150, item.HeightMm, 0);
    }

    [Fact]
    public void Pdf_HasPagesInPaperSize_AndAllImages()
    {
        using var photo = new MagickImage(MagickColors.SkyBlue, 1181, 1772);              // 10 × 15 cm
        using var square = new MagickImage(MagickColors.Orange, 709, 709);                // 6 × 6 cm
        using var transparent = new MagickImage(MagickColors.Transparent, 472, 472);      // 4 × 4 cm, PNG
        using var photo2 = new MagickImage(MagickColors.SeaGreen, 1181, 1772);            // second photo
        // (PDFsharp embeds identical images only once — hence four different ones)
        var sources = new Dictionary<int, (MagickImage Image, ExportOptions Options)>
        {
            [1] = (photo, Opt(1181, 1772)),
            [2] = (square, Opt(709, 709)),
            [3] = (transparent, Opt(472, 472)),
            [4] = (photo2, Opt(1181, 1772)),
        };

        var items = sources.Select(s => PrintDocument.ToItem(s.Key, s.Value.Options)).ToList();
        var layout = PrintLayout.Arrange(items, new LayoutOptions(
            PaperSize.Common[0], PageOrientation.Auto, 5, 4, true, PackingMode.CutFriendly, true));

        using var pdf = new MemoryStream();
        PrintDocument.WritePdf(layout,
            p => PrintDocument.RenderForPrint(sources[p.ItemId].Image, sources[p.ItemId].Options, p),
            pdf, cutMarks: true, gapMm: 4);

        pdf.Position = 0;
        using var read = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);

        Assert.Equal(layout.Pages.Count, read.PageCount);
        for (var i = 0; i < read.PageCount; i++)
        {
            var page = read.Pages[i];
            var expected = layout.Pages[i];
            Assert.Equal(expected.WidthMm, page.Width.Millimeter, 1);
            Assert.Equal(expected.HeightMm, page.Height.Millimeter, 1);
            Assert.Equal(expected.Placements.Count, ImagesOn(page));
        }
    }

    [Fact]
    public void RotatedPlacement_RendersImageTurned()
    {
        using var landscape = new MagickImage(MagickColors.SkyBlue, 300, 200);
        var p = new Placement(1, 0, 0, 200 / 300.0 * 25.4, 300 / 300.0 * 25.4, Rotated: true, Scale: 1);

        using var back = new MagickImage(PrintDocument.RenderForPrint(landscape, Opt(300, 200), p));

        Assert.Equal((200u, 300u), (back.Width, back.Height));
    }

    [Fact]
    public void ShrunkPlacement_RendersFewerPixels_SameDpi()
    {
        using var big = new MagickImage(MagickColors.SkyBlue, 1000, 800);
        var p = new Placement(1, 0, 0, 1, 1, Rotated: false, Scale: 0.5);

        using var back = new MagickImage(PrintDocument.RenderForPrint(big, Opt(1000, 800), p));

        Assert.Equal((500u, 400u), (back.Width, back.Height));
    }
}
