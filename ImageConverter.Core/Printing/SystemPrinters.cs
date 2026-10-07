using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.Versioning;

namespace ImageConverter.Core.Printing;

public sealed record PrinterInfo(string Name, bool IsDefault)
{
    public override string ToString() => IsDefault ? $"{Name} (Standard)" : Name;
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

    // ------------------------------------------------------------ Drucken

    /// <summary>Linux/macOS: fertiges PDF an CUPS schicken.</summary>
    public static Task PrintPdfWithCupsAsync(string pdfPath, string printer, int copies, PaperSize paper, string title) =>
        Task.Run(() =>
        {
            var args = new List<string>
            {
                "-d", printer,
                "-n", Math.Max(1, copies).ToString(),
                "-t", title,
                "-o", $"media={CupsMedia(paper)}",
                "-o", "print-scaling=none",    // exakt in mm, nicht „an Seite anpassen"
                "-o", "fit-to-page=false",
                pdfPath,
            };
            var (code, output) = RunWithExit("lp", args);
            if (code != 0) throw new InvalidOperationException($"Drucken fehlgeschlagen: {output.Trim()}");
        });

    internal static string CupsMedia(PaperSize paper) => paper.Name switch
    {
        "Foto 10 × 15 cm" => "4x6",
        "Foto 13 × 18 cm" => "5x7",
        _ => paper.Name,   // A4, A5, A3, Letter, Legal heißen bei CUPS genauso
    };

    /// <summary>Windows: Seitenplan direkt über den Druckdienst drucken.</summary>
    [SupportedOSPlatform("windows")]
    public static void PrintWithWindows(LayoutResult layout, Func<Placement, byte[]> imageFor,
        string printer, int copies, PaperSize paper, bool cutMarks, double gapMm, string title)
    {
        using var doc = new System.Drawing.Printing.PrintDocument { DocumentName = title };
        doc.PrinterSettings.PrinterName = printer;
        doc.PrinterSettings.Copies = (short)Math.Clamp(copies, 1, short.MaxValue);
        doc.DefaultPageSettings.Margins = new System.Drawing.Printing.Margins(0, 0, 0, 0);

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

    private static (int Code, string Output) RunWithExit(string file, IEnumerable<string> args)
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

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"{file} ließ sich nicht starten.");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, stdout + stderr);
    }
}
