using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;

namespace ImageConverter.Core.Printing;

public sealed record PrinterInfo(string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? Tr.T("{0} (Standard)", Name) : Name;
}

public enum DuplexMode
{
    OneSided,
    /// <summary>Wenden an der langen Kante — wie ein Buch im Hochformat.</summary>
    LongEdge,
    /// <summary>Wenden an der kurzen Kante — wie ein Kalender oder Klemmbrett.</summary>
    ShortEdge,
}

/// <summary>Druckeroptionen, die für jeden Auftrag gelten.</summary>
public sealed record PrintSettings(int Copies, DuplexMode Duplex, bool Grayscale);

/// <summary>
/// Was ein Drucker kann. null = unbekannt (das System verrät es nicht) — die Option
/// bleibt dann wählbar; false = sicher nicht vorhanden — die Option wird ausgegraut.
/// </summary>
public sealed record PrinterCapabilities(bool? Duplex, bool? Color)
{
    public static PrinterCapabilities Unknown { get; } = new(null, null);
}

/// <summary>
/// Installierte Drucker und Druckaufträge. Linux: CUPS über die Befehle <c>lpstat</c>
/// und <c>lp</c> — das PDF geht unverändert an CUPS. Windows: Druckdienst über
/// System.Drawing.Printing. In beiden Fällen ohne Skalierung, damit die Millimeter
/// aus dem Seitenplan exakt gedruckt werden.
/// </summary>
public static class SystemPrinters
{
    // ------------------------------------------------------------ Drucker

    public static IReadOnlyList<PrinterInfo> List()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return ListWindows();
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) return ListCups();
        }
        catch
        {
            // Kein Druckdienst erreichbar — dann bleibt „Als PDF speichern"
        }
        return [];
    }

    private static IReadOnlyList<PrinterInfo> ListCups()
    {
        // LC_ALL=C: Ausgabe von lpstat ist sonst übersetzt („Standardziel des Systems: …")
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
    /// Kleinster Rand in mm, den der Drucker technisch nicht bedrucken kann — sofern
    /// das System ihn verrät (Windows). Unter Linux liefert CUPS ihn nicht zuverlässig: null.
    /// </summary>
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
        var area = page.PrintableArea;   // Hundertstel Zoll
        var right = page.PaperSize.Width - area.Right;
        var bottom = page.PaperSize.Height - area.Bottom;
        var max = new[] { area.Left, area.Top, right, bottom }.Max();
        return Math.Round(max * 0.254, 1);
    }

    // ------------------------------------------------------------ Fähigkeiten

    /// <summary>Kann der Drucker doppelseitig, kann er Farbe? Soweit das System es verrät.</summary>
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
            // Nicht ermittelbar — Optionen bleiben wählbar
        }
        return PrinterCapabilities.Unknown;
    }

    /// <summary>
    /// Wertet die Ausgabe von <c>lpoptions -p NAME -l</c> aus. Zeilen wie
    /// „sides/2-Sided Printing: *one-sided two-sided-long-edge two-sided-short-edge"
    /// (IPP Everywhere) oder „Duplex/Double-Sided: *None DuplexNoTumble DuplexTumble" (PPD).
    /// </summary>
    internal static PrinterCapabilities ParseCupsCapabilities(string lpoptionsOutput)
    {
        bool? duplex = null, color = null;

        foreach (var raw in lpoptionsOutput.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            // „Schlüssel/Anzeigetext: Werte" — der Schlüssel endet am ersten „/" vor dem „:"
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
                // Nur Grau angeboten → reiner Schwarzweißdrucker
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

    // ------------------------------------------------------------ Drucken

    /// <summary>Linux/macOS: fertiges PDF an CUPS schicken.</summary>
    public static Task PrintPdfWithCupsAsync(string pdfPath, string printer, PrintSettings settings, PaperSize paper, string title) =>
        Task.Run(() =>
        {
            var (code, output) = RunWithExit("lp", BuildCupsArgs(pdfPath, printer, settings, paper, title));
            if (code != 0) throw new InvalidOperationException(Tr.T("Drucken fehlgeschlagen: {0}", output.Trim()));
        });

    /// <summary>Befehlszeile für <c>lp</c> — mit den Standard-IPP-Optionen, die CUPS
    /// auch für ältere PPD-Treiber übersetzt.</summary>
    internal static List<string> BuildCupsArgs(string pdfPath, string printer, PrintSettings settings, PaperSize paper, string title) =>
    [
        "-d", printer,
        "-n", Math.Max(1, settings.Copies).ToString(),
        "-t", title,
        "-o", $"media={CupsMedia(paper)}",
        "-o", "print-scaling=none",    // exakt in mm, nicht „an Seite anpassen"
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
        _ => paper.Name,   // A4, A5, A3, Letter, Legal heißen bei CUPS genauso
    };

    /// <summary>Windows: Seitenplan direkt über den Druckdienst drucken.</summary>
    [SupportedOSPlatform("windows")]
    public static void PrintWithWindows(LayoutResult layout, Func<Placement, byte[]> imageFor,
        string printer, PrintSettings settings, PaperSize paper, bool cutMarks, double gapMm, string title)
    {
        using var doc = new System.Drawing.Printing.PrintDocument { DocumentName = title };
        doc.PrinterSettings.PrinterName = printer;
        doc.PrinterSettings.Copies = (short)Math.Clamp(settings.Copies, 1, short.MaxValue);
        doc.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(0, 0, 0, 0);

        // Doppelseitig: Windows nennt lange Kante „Vertical", kurze Kante „Horizontal"
        if (doc.PrinterSettings.CanDuplex)
            doc.PrinterSettings.Duplex = settings.Duplex switch
            {
                DuplexMode.LongEdge => System.Drawing.Printing.Duplex.Vertical,
                DuplexMode.ShortEdge => System.Drawing.Printing.Duplex.Horizontal,
                _ => System.Drawing.Printing.Duplex.Simplex,
            };
        if (doc.PrinterSettings.SupportsColor)
            doc.DefaultPageSettings.Color = !settings.Grayscale;

        // Passendes Papier des Druckers suchen (Maße in Hundertstel Zoll, ±2 mm)
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

            // Der Ursprung liegt am Anfang der bedruckbaren Fläche, nicht an der
            // Blattkante — um den technischen Rand zurückschieben, damit mm stimmen.
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

    // ------------------------------------------------------------ Hilfen

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
        // Erst parallel lesen, dann begrenzt warten: ReadToEnd vor dem Warten blockiert
        // unbegrenzt, wenn der Druckdienst hängt.
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
