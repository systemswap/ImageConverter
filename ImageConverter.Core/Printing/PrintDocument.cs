using ImageConverter.Core.Imaging;
using ImageMagick;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ImageConverter.Core.Printing;

/// <summary>
/// Vom Seitenplan zum Druck: Druckmaße aus den Einstellungen, Bilder für den Druck
/// aufbereiten und das PDF schreiben. Das PDF dient zugleich als „Als PDF speichern"
/// und — unter Linux — als Druckauftrag für CUPS.
/// </summary>
public static class PrintDocument
{
    private const double MmPerInch = 25.4;

    /// <summary>Druckgröße eines Bildes: Pixelmaße ÷ DPI — genau das, was in „Größe" steht.</summary>
    public static PrintItem ToItem(int id, ExportOptions o) =>
        new(id, o.Width / (double)Math.Max(1, o.Dpi) * MmPerInch, o.Height / (double)Math.Max(1, o.Dpi) * MmPerInch);

    /// <summary>
    /// Bild für eine Platzierung: dieselbe Kette wie beim Export (Größe, Entrauschen,
    /// Schärfen), für den Druck immer sRGB; bei Bedarf verkleinert und gedreht.
    /// Ergebnis: JPEG (Qualität 95, ohne Farb-Unterabtastung) oder PNG bei Transparenz.
    /// </summary>
    public static byte[] RenderForPrint(MagickImage source, ExportOptions o, Placement p)
    {
        var options = o with { Color = ColorMode.ConvertToSrgb, Metadata = MetadataMode.RemoveAll };
        if (p.Scale < 1)
        {
            // Verkleinert gedruckt → auch in passend weniger Pixeln rechnen (gleiche DPI)
            options = options with
            {
                Width = Math.Max(1, (int)Math.Round(o.Width * p.Scale)),
                Height = Math.Max(1, (int)Math.Round(o.Height * p.Scale)),
                SharpenSigma = o.SharpenSigma * p.Scale,
            };
        }

        using var img = ImageExporter.Render(source, options);
        if (p.Rotated)
        {
            img.Rotate(90);
            img.ResetPage();
        }

        // PDFsharp liest nur gängige Varianten: 8 Bit pro Kanal, Farbe (kein Grau/Palette).
        // Mit Q16 schriebe ImageMagick sonst z. B. 16-Bit-PNG — „Unsupported image format".
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
            img.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "4:4:4");   // volle Farbauflösung
        }

        return img.ToByteArray();
    }

    /// <summary>
    /// Schreibt den Seitenplan als PDF. <paramref name="imageFor"/> liefert das fertig
    /// kodierte Bild einer Platzierung (siehe <see cref="RenderForPrint"/>).
    /// </summary>
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
    /// Feine Eckmarken außerhalb des Bildes, im Abstand zwischen den Bildern. Sie
    /// zeigen, wo geschnitten wird, ohne ins Bild zu ragen. Bei zu kleinem Abstand
    /// gibt es keinen Platz dafür — dann keine Marken.
    /// </summary>
    private static void DrawCutMarks(XGraphics gfx, Placement p, double gapMm)
    {
        const double offset = 0.6;                      // Luft zwischen Bild und Marke
        var length = Math.Min(4.0, gapMm / 2 - offset); // nie bis zum Nachbarbild
        if (length < 0.8) return;

        var pen = new XPen(XColor.FromArgb(255, 90, 90, 90), Pt(0.12));
        double l = p.X, t = p.Y, r = p.X + p.Width, b = p.Y + p.Height;

        foreach (var (x, y, dx, dy) in new[] { (l, t, -1, -1), (r, t, 1, -1), (l, b, -1, 1), (r, b, 1, 1) })
        {
            // waagerechte Marke in Verlängerung der Kante, senkrechte ebenso
            gfx.DrawLine(pen, Pt(x + dx * offset), Pt(y), Pt(x + dx * (offset + length)), Pt(y));
            gfx.DrawLine(pen, Pt(x), Pt(y + dy * offset), Pt(x), Pt(y + dy * (offset + length)));
        }
    }
}
