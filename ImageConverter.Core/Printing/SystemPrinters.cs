using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;

namespace ImageConverter.Core.Printing;

/// <summary>An installed printer.</summary>
/// <param name="Name">Name of the printer as known to the print service.</param>
/// <param name="IsDefault">Whether it is the system's default printer.</param>
public sealed record PrinterInfo(string Name, bool IsDefault)
{
    /// <summary>Returns the name, marked as default where applicable.</summary>
    /// <returns>The display text in the current language.</returns>
    public override string ToString() => IsDefault ? Tr.T("{0} (Standard)", Name) : Name;
}

/// <summary>Two-sided printing mode.</summary>
public enum DuplexMode
{
    /// <summary>One-sided printing.</summary>
    OneSided,
    /// <summary>Flip on the long edge, like a book in portrait format.</summary>
    LongEdge,
    /// <summary>Flip on the short edge, like a calendar or clipboard.</summary>
    ShortEdge,
}

/// <summary>Printer options that apply to a whole print job.</summary>
/// <param name="Copies">Number of copies; values below 1 count as 1.</param>
/// <param name="Duplex">Two-sided printing mode.</param>
/// <param name="Grayscale">Whether to print in black and white.</param>
public sealed record PrintSettings(int Copies, DuplexMode Duplex, bool Grayscale);

/// <summary>
/// What a printer supports.
/// </summary>
/// <remarks>
/// <see langword="null"/> = unknown (the system does not report it), so the option remains selectable;
/// <see langword="false"/> = definitely not available, so the option is disabled.
/// </remarks>
/// <param name="Duplex">Whether the printer can print on both sides.</param>
/// <param name="Color">Whether the printer can print in colour.</param>
public sealed record PrinterCapabilities(bool? Duplex, bool? Color)
{
    /// <summary>Capabilities when nothing is known.</summary>
    public static PrinterCapabilities Unknown { get; } = new(null, null);
}

/// <summary>
/// Installed printers and print jobs.
/// </summary>
/// <remarks>
/// Linux and macOS: CUPS through the commands <c>lpstat</c>, <c>lpoptions</c> and <c>lp</c>; the PDF is sent
/// to CUPS unchanged. Windows: the print service through System.Drawing.Printing. In both cases without
/// scaling, so that the millimetres of the page layout are printed exactly.
/// </remarks>
public static class SystemPrinters
{
    // ------------------------------------------------------------ Printers

    /// <summary>Lists the installed printers.</summary>
    /// <returns>The printers, default printer first, then by name; empty if no print service is reachable.</returns>
    public static IReadOnlyList<PrinterInfo> List()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ListWindows();
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return ListCups();
        }
        catch
        {
            // No print service reachable; "Save as PDF" remains available.
        }
        return [];
    }

    private static IReadOnlyList<PrinterInfo> ListCups()
    {
        // RunWithExit sets LC_ALL=C; otherwise the lpstat output is translated ("Standardziel des Systems: …").
        var names = Run("lpstat", "-e").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var defaultLine = Run("lpstat", "-d");
        var defaultName = defaultLine.Contains(':') ? defaultLine[(defaultLine.IndexOf(':') + 1)..].Trim() : "";

        return names.Distinct()
            .Select(n => new PrinterInfo(n, n == defaultName))
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<PrinterInfo> ListWindows()
    {
        var defaultName = new System.Drawing.Printing.PrinterSettings().PrinterName;
        return System.Drawing.Printing.PrinterSettings.InstalledPrinters.Cast<string>()
            .Select(n => new PrinterInfo(n, n == defaultName))
            .OrderByDescending(p => p.IsDefault).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Returns the margin in millimetres that the printer cannot print on for technical reasons, if the
    /// system reports it (Windows).
    /// </summary>
    /// <param name="printer">Name of the printer.</param>
    /// <returns>
    /// The largest of the four hardware margins, rounded to 0.1 mm; <see langword="null"/> on other systems
    /// (CUPS does not report it reliably) or on errors.
    /// </returns>
    public static double? HardwareMarginMm(string printer)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try { return HardwareMarginWindows(printer); }
        catch { return null; }
    }

    [SupportedOSPlatform("windows")]
    private static double HardwareMarginWindows(string printer)
    {
        var settings = new System.Drawing.Printing.PrinterSettings { PrinterName = printer };
        var page = settings.DefaultPageSettings;
        var area = page.PrintableArea;   // hundredths of an inch
        var right = page.PaperSize.Width - area.Right;
        var bottom = page.PaperSize.Height - area.Bottom;
        var max = new[] { area.Left, area.Top, right, bottom }.Max();
        return Math.Round(max * 0.254, 1);
    }

    // ------------------------------------------------------------ Capabilities

    /// <summary>Determines whether the printer supports two-sided printing and colour, as far as the system reports it.</summary>
    /// <param name="printer">Name of the printer.</param>
    /// <returns>The capabilities; <see cref="PrinterCapabilities.Unknown"/> if they cannot be determined.</returns>
    public static PrinterCapabilities Capabilities(string printer)
    {
        try
        {
            if (OperatingSystem.IsWindows()) return CapabilitiesWindows(printer);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                return ParseCupsCapabilities(RunWithExit("lpoptions", ["-p", printer, "-l"]).Output);
        }
        catch
        {
            // Cannot be determined; the options remain selectable.
        }
        return PrinterCapabilities.Unknown;
    }

    /// <summary>
    /// Parses the output of <c>lpoptions -p NAME -l</c>, with lines such as
    /// "sides/2-Sided Printing: *one-sided two-sided-long-edge two-sided-short-edge" (IPP Everywhere) or
    /// "Duplex/Double-Sided: *None DuplexNoTumble DuplexTumble" (PPD).
    /// </summary>
    internal static PrinterCapabilities ParseCupsCapabilities(string lpoptionsOutput)
    {
        bool? duplex = null, color = null;

        foreach (var raw in lpoptionsOutput.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            // "Key/Display text: values"; the key ends at the first "/" before the ":".
            var slash = line.IndexOf('/');
            var keyEnd = slash > 0 && slash < colon ? slash : colon;
            var key = line[..keyEnd].ToLowerInvariant();
            var values = line[(colon + 1)..].Replace("*", "").ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (key is "sides" or "duplex")
            {
                duplex = values.Any(v => v.Contains("two-sided") || v.Contains("duplexnotumble") || v.Contains("duplextumble"));
            }
            else if (key is "print-color-mode" or "colormodel" or "colormode")
            {
                var hasColor = values.Any(v => v is "color" or "rgb" or "cmyk" or "rgbw" || v.Contains("color") && !v.Contains("gray"));
                var hasGray = values.Any(v => v.Contains("monochrome") || v.Contains("gray") || v.Contains("grey") || v is "black" or "kgray");
                // Only greyscale offered, so a black-and-white-only printer.
                color = hasColor ? true : hasGray ? false : null;
            }
        }

        return new PrinterCapabilities(duplex, color);
    }

    [SupportedOSPlatform("windows")]
    private static PrinterCapabilities CapabilitiesWindows(string printer)
    {
        var s = new System.Drawing.Printing.PrinterSettings { PrinterName = printer };
        return new PrinterCapabilities(s.CanDuplex, s.SupportsColor);
    }

    // ------------------------------------------------------------ Printing

    /// <summary>Linux/macOS: sends a finished PDF to CUPS.</summary>
    /// <param name="pdfPath">Path of the PDF file.</param>
    /// <param name="printer">Name of the printer.</param>
    /// <param name="settings">Copies, two-sided printing and black and white.</param>
    /// <param name="paper">Paper size, passed as the CUPS media name.</param>
    /// <param name="title">Title of the print job.</param>
    /// <returns>A task that runs <c>lp</c> on a worker thread.</returns>
    /// <exception cref="InvalidOperationException"><c>lp</c> ended with an error, or could not be started.</exception>
    /// <exception cref="TimeoutException"><c>lp</c> did not finish within 15 s.</exception>
    public static Task PrintPdfWithCupsAsync(string pdfPath, string printer, PrintSettings settings, PaperSize paper, string title) =>
        Task.Run(() =>
        {
            var (code, output) = RunWithExit("lp", BuildCupsArgs(pdfPath, printer, settings, paper, title));
            if (code != 0) throw new InvalidOperationException(Tr.T("Drucken fehlgeschlagen: {0}", output.Trim()));
        });

    /// <summary>Builds the arguments for <c>lp</c>, using the standard IPP options that CUPS also translates
    /// for older PPD drivers.</summary>
    internal static List<string> BuildCupsArgs(string pdfPath, string printer, PrintSettings settings, PaperSize paper, string title) =>
    [
        "-d", printer,
        "-n", Math.Max(1, settings.Copies).ToString(),
        "-t", title,
        "-o", $"media={CupsMedia(paper)}",
        "-o", "print-scaling=none",    // exact in mm, not "fit to page"
        "-o", "fit-to-page=false",
        "-o", "sides=" + settings.Duplex switch
        {
            DuplexMode.LongEdge => "two-sided-long-edge",
            DuplexMode.ShortEdge => "two-sided-short-edge",
            _ => "one-sided",
        },
        "-o", "print-color-mode=" + (settings.Grayscale ? "monochrome" : "color"),
        pdfPath,
    ];

    internal static string CupsMedia(PaperSize paper) => paper.Name switch
    {
        "Foto 10 × 15 cm" => "4x6",
        "Foto 13 × 18 cm" => "5x7",
        _ => paper.Name,   // A4, A5, A3, Letter and Legal have the same names in CUPS
    };

    /// <summary>Windows: prints the page layout directly through the print service.</summary>
    /// <param name="layout">Pages with placements in millimetres.</param>
    /// <param name="imageFor">Returns the encoded image of a placement (see <see cref="PrintDocument.RenderForPrint"/>).</param>
    /// <param name="printer">Name of the printer.</param>
    /// <param name="settings">Copies, two-sided printing and black and white.</param>
    /// <param name="paper">Paper size; the printer's paper within ±2 mm is selected if available.</param>
    /// <param name="cutMarks">Whether to draw cut marks at the image corners.</param>
    /// <param name="gapMm">Gap between the images in millimetres; determines the length of the cut marks.</param>
    /// <param name="title">Document name of the print job.</param>
    [SupportedOSPlatform("windows")]
    public static void PrintWithWindows(LayoutResult layout, Func<Placement, byte[]> imageFor,
        string printer, PrintSettings settings, PaperSize paper, bool cutMarks, double gapMm, string title)
    {
        using var doc = new System.Drawing.Printing.PrintDocument { DocumentName = title };
        doc.PrinterSettings.PrinterName = printer;
        doc.PrinterSettings.Copies = (short)Math.Clamp(settings.Copies, 1, short.MaxValue);
        doc.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(0, 0, 0, 0);

        // Two-sided: Windows calls the long edge "Vertical" and the short edge "Horizontal".
        if (doc.PrinterSettings.CanDuplex)
            doc.PrinterSettings.Duplex = settings.Duplex switch
            {
                DuplexMode.LongEdge => System.Drawing.Printing.Duplex.Vertical,
                DuplexMode.ShortEdge => System.Drawing.Printing.Duplex.Horizontal,
                _ => System.Drawing.Printing.Duplex.Simplex,
            };
        if (doc.PrinterSettings.SupportsColor)
            doc.DefaultPageSettings.Color = !settings.Grayscale;

        // Find the matching paper of the printer (dimensions in hundredths of an inch, ±2 mm).
        var match = doc.PrinterSettings.PaperSizes.Cast<System.Drawing.Printing.PaperSize>()
            .FirstOrDefault(s =>
                Math.Abs(s.Width * 0.254 - Math.Min(paper.WidthMm, paper.HeightMm)) < 2 &&
                Math.Abs(s.Height * 0.254 - Math.Max(paper.WidthMm, paper.HeightMm)) < 2);
        if (match is not null) doc.DefaultPageSettings.PaperSize = match;

        var index = 0;
        doc.QueryPageSettings += (_, e) =>
        {
            var page = layout.Pages[index];
            e.PageSettings.Landscape = page.WidthMm > page.HeightMm;
            if (doc.PrinterSettings.SupportsColor) e.PageSettings.Color = !settings.Grayscale;
        };

        doc.PrintPage += (_, e) =>
        {
            var page = layout.Pages[index];
            var g = e.Graphics!;
            g.PageUnit = GraphicsUnit.Millimeter;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // The origin is at the start of the printable area, not at the paper edge; shift back by the
            // hardware margin so that the millimetres are correct.
            var offX = e.PageSettings.HardMarginX * 0.254f;
            var offY = e.PageSettings.HardMarginY * 0.254f;

            foreach (var p in page.Placements)
            {
                using var ms = new MemoryStream(imageFor(p));
                using var bmp = new Bitmap(ms);
                g.DrawImage(bmp, new RectangleF((float)p.X - offX, (float)p.Y - offY, (float)p.Width, (float)p.Height));
            }

            if (cutMarks)
            {
                using var pen = new Pen(Color.FromArgb(90, 90, 90), 0.12f);
                const float offset = 0.6f;
                var length = (float)Math.Min(4.0, gapMm / 2 - offset);
                if (length >= 0.8f)
                {
                    foreach (var p in page.Placements)
                    {
                        float l = (float)p.X - offX, t = (float)p.Y - offY;
                        float r = l + (float)p.Width, b = t + (float)p.Height;
                        foreach (var (x, y, dx, dy) in new[] { (l, t, -1f, -1f), (r, t, 1f, -1f), (l, b, -1f, 1f), (r, b, 1f, 1f) })
                        {
                            g.DrawLine(pen, x + dx * offset, y, x + dx * (offset + length), y);
                            g.DrawLine(pen, x, y + dy * offset, x, y + dy * (offset + length));
                        }
                    }
                }
            }

            index++;
            e.HasMorePages = index < layout.Pages.Count;
        };

        doc.Print();
    }

    // ------------------------------------------------------------ Helpers

    private static string Run(string file, string args) => RunWithExit(file, args.Split(' ')).Output;

    internal static (int Code, string Output) RunWithExit(string file, IEnumerable<string> args, int timeoutMs = 15000)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["LC_ALL"] = "C";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException(Tr.T("{0} ließ sich nicht starten.", file));
        // Read both streams in parallel first, then wait with a time limit: ReadToEnd before waiting
        // blocks indefinitely if the print service hangs.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException(Tr.T("{0} antwortet nicht.", file));
        }
        return (p.ExitCode, stdout.Result + stderr.Result);
    }
}
