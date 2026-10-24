using ImageConverter.Core.Imaging;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ImageConverter.Core.Printing;

/// <summary>
/// Turns the page layout into a print: print sizes from the settings, images prepared for printing, and the PDF.
/// </summary>
/// <remarks>
/// The PDF serves both for "Save as PDF" and, on Linux, as the print job for CUPS.
/// </remarks>
public static class PrintDocument
{
    private const double MmPerInch = 25.4;

    /// <summary>Computes the print size of an image: pixel dimensions ÷ DPI, exactly what the "Size" group shows.</summary>
    /// <param name="id">Identifier of the image in the layout.</param>
    /// <param name="o">Export settings of the image (pixel size and DPI; a DPI below 1 counts as 1).</param>
    /// <returns>The item with width and height in millimetres.</returns>
    public static PrintItem ToItem(int id, ExportOptions o) =>
        new(id, o.Width / (double)Math.Max(1, o.Dpi) * MmPerInch, o.Height / (double)Math.Max(1, o.Dpi) * MmPerInch);

    /// <summary>
    /// Renders the image for one placement with the same chain as the export (size, denoising, sharpening),
    /// always in sRGB and without metadata; scaled down and rotated as required.
    /// </summary>
    /// <param name="source">Source image; it is not modified.</param>
    /// <param name="o">Export settings of the image.</param>
    /// <param name="p">Placement on the page; <see cref="Placement.Scale"/> below 1 reduces the pixel size at the same DPI.</param>
    /// <param name="grayscale">Black and white: converts the image itself to greyscale, which also works when a
    /// printer driver ignores the "monochrome" option, and in the PDF.</param>
    /// <returns>JPEG (quality 95, without chroma subsampling), or PNG if the image has transparency; 8 bits per channel.</returns>
    public static byte[] RenderForPrint(MagickImage source, ExportOptions o, Placement p, bool grayscale = false)
    {
        var options = o with { Color = ColorMode.ConvertToSrgb, Metadata = MetadataMode.RemoveAll };
        if (p.Scale < 1)
        {
            // Printed smaller, so also render with correspondingly fewer pixels (same DPI).
            options = options with
            {
                Width = Math.Max(1, (int)Math.Round(o.Width * p.Scale)),
                Height = Math.Max(1, (int)Math.Round(o.Height * p.Scale)),
                SharpenSigma = o.SharpenSigma * p.Scale,
                GrainScale = o.GrainScale * p.Scale,   // grain as on the scaled-down export
            };
        }

        using var img = ImageExporter.Render(source, options);
        if (p.Rotated)
        {
            img.Rotate(90);
            img.ResetPage();
        }

        // Brightness as perceived by the eye (ITU-R BT.709 luminance), not the plain average.
        if (grayscale)
            img.Grayscale(PixelIntensityMethod.Rec709Luminance);

        // PDFsharp reads only common variants: 8 bits per channel, colour (no greyscale or palette).
        // With Q16, ImageMagick would otherwise write for example a 16-bit PNG, and PDFsharp fails with
        // "Unsupported image format".
        img.Depth = 8;
        if (img.HasAlpha)
        {
            img.ColorType = ColorType.TrueColorAlpha;
            img.Format = MagickFormat.Png32;
        }
        else
        {
            img.ColorType = ColorType.TrueColor;
            img.Format = MagickFormat.Jpeg;
            img.Quality = 95;
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:4:4");   // full colour resolution
        }

        return img.ToByteArray();
    }

    /// <summary>
    /// Writes the page layout as a PDF.
    /// </summary>
    /// <param name="layout">Pages with placements in millimetres.</param>
    /// <param name="imageFor">Returns the encoded image of a placement (see <see cref="RenderForPrint"/>).</param>
    /// <param name="output">Target stream; it stays open.</param>
    /// <param name="cutMarks">Whether to draw cut marks at the image corners.</param>
    /// <param name="gapMm">Gap between the images in millimetres; determines the length of the cut marks.</param>
    /// <param name="title">Document title in the PDF metadata.</param>
    public static void WritePdf(LayoutResult layout, Func<Placement, byte[]> imageFor, Stream output,
        bool cutMarks, double gapMm, string title = "ImageConverter")
    {
        using var doc = new PdfDocument();
        doc.Info.Title = title;
        doc.Info.Creator = "ImageConverter";

        foreach (var page in layout.Pages)
        {
            var pdfPage = doc.AddPage();
            pdfPage.Width = XUnit.FromMillimeter(page.WidthMm);
            pdfPage.Height = XUnit.FromMillimeter(page.HeightMm);

            using var gfx = XGraphics.FromPdfPage(pdfPage);
            foreach (var p in page.Placements)
            {
                using var ms = new MemoryStream(imageFor(p));
                using var image = XImage.FromStream(ms);
                gfx.DrawImage(image, Pt(p.X), Pt(p.Y), Pt(p.Width), Pt(p.Height));
            }

            if (cutMarks)
                foreach (var p in page.Placements)
                    DrawCutMarks(gfx, p, gapMm);
        }

        doc.Save(output, closeStream: false);
    }

    private static double Pt(double mm) => XUnit.FromMillimeter(mm).Point;

    /// <summary>
    /// Draws fine corner marks outside the image, in the gap between the images. They show where to cut without
    /// reaching into the image. If the gap is too small, there is no room for them and no marks are drawn.
    /// </summary>
    private static void DrawCutMarks(XGraphics gfx, Placement p, double gapMm)
    {
        const double offset = 0.6;                      // space between image and mark (mm)
        var length = Math.Min(4.0, gapMm / 2 - offset); // never reaches the neighbouring image
        if (length < 0.8) return;

        var pen = new XPen(XColor.FromArgb(255, 90, 90, 90), Pt(0.12));
        double l = p.X, t = p.Y, r = p.X + p.Width, b = p.Y + p.Height;

        foreach (var (x, y, dx, dy) in new[] { (l, t, -1, -1), (r, t, 1, -1), (l, b, -1, 1), (r, b, 1, 1) })
        {
            // horizontal mark extending the edge, likewise a vertical one
            gfx.DrawLine(pen, Pt(x + dx * offset), Pt(y), Pt(x + dx * (offset + length)), Pt(y));
            gfx.DrawLine(pen, Pt(x), Pt(y + dy * offset), Pt(x), Pt(y + dy * (offset + length)));
        }
    }
}
