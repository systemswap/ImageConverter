using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Printing;
using ImageConverter.Imaging;

namespace ImageConverter.ViewModels;

/// <summary>Entry in the printer list; "Save as PDF" is an entry of its own.</summary>
/// <param name="Label">Display name; for the PDF entry the German key that is translated for display.</param>
/// <param name="Printer">The printer; null for "Save as PDF".</param>
public sealed record PrinterChoice(string Label, PrinterInfo? Printer)
{
    public bool IsPdf => Printer is null;
    public override string ToString() => IsPdf ? Tr.T(Label) : Label;   // "Save as PDF" is translated, printer names are not
}

/// <summary>An image to print: print job (size, DPI …) and thumbnail.</summary>
/// <param name="Id">Identifier of the image within the layout.</param>
/// <param name="Path">Path of the source file.</param>
/// <param name="Options">Export options of the image's settings state.</param>
/// <param name="Thumbnail">Thumbnail for the page preview; null if none.</param>
public sealed record PrintSourceInfo(int Id, string Path, ExportOptions Options, Bitmap? Thumbnail);

/// <summary>
/// Print dialog: settings on the left, page preview on the right. <see cref="PrintLayout"/>
/// recomputes the arrangement on every change (milliseconds).
/// </summary>
public partial class PrintDialogViewModel : ViewModelBase
{
    private const int ThumbSize = 320;

    private readonly MainViewModel _main;
    private readonly List<PrintSourceInfo> _sources = [];

    public PrintDialogViewModel(MainViewModel main)
    {
        _main = main;

        // Fetch the printers in the background, because lpstat or the Windows print service can take long or
        // hang. Until then "Save as PDF" is shown.
        Printers = [new PrinterChoice("Als PDF speichern", null)];
        SelectedPrinter = Printers[0];
        _ = LoadPrintersAsync();

        Papers = PaperSize.Common;
        SelectedPaper = Papers[0];

        SelectedOrientation = Orientations[0];
        SelectedMode = Modes[0];
        SelectedColorMode = ColorModes[0];
        SelectedDuplex = DuplexModes[0];

        PrintAll = main.OpenFiles.Count > 1;
        _ = LoadCapabilitiesAsync(SelectedPrinter);
    }

    // ------------------------------------------------------------ Settings

    public ObservableCollection<PrinterChoice> Printers { get; }
    public IReadOnlyList<PaperSize> Papers { get; }
    // The values are the German texts (used as keys); the translation is displayed (ComboBox.tr).
    internal static readonly string[] OrientationLabels = ["Automatisch", "Hochformat", "Querformat"];
    internal static readonly string[] ModeLabels = ["Mehrere pro Seite — schneidefreundlich", "Mehrere pro Seite — möglichst dicht", "Ein Bild pro Seite"];
    internal static readonly string[] ColorModeLabels = ["Farbe", "Schwarzweiß"];
    internal static readonly string[] DuplexLabels = ["Einseitig", "Doppelseitig — lange Kante", "Doppelseitig — kurze Kante"];

    public IReadOnlyList<string> Orientations => OrientationLabels;
    public IReadOnlyList<string> Modes => ModeLabels;
    public IReadOnlyList<string> ColorModes => ColorModeLabels;
    public IReadOnlyList<string> DuplexModes => DuplexLabels;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText), nameof(MarginHint), nameof(CopiesAvailable),
        nameof(DuplexAvailable), nameof(DuplexHint))]
    public partial PrinterChoice SelectedPrinter { get; set; }

    [ObservableProperty] public partial PaperSize SelectedPaper { get; set; }
    [ObservableProperty] public partial string SelectedOrientation { get; set; }
    [ObservableProperty] public partial string SelectedMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarginHint))]
    public partial decimal MarginMm { get; set; } = 5;

    [ObservableProperty] public partial decimal GapMm { get; set; } = 4;
    [ObservableProperty] public partial bool AllowRotation { get; set; } = true;
    [ObservableProperty] public partial bool ShrinkToFit { get; set; } = true;
    [ObservableProperty] public partial bool CutMarks { get; set; } = true;
    [ObservableProperty] public partial decimal Copies { get; set; } = 1;
    [ObservableProperty] public partial string SelectedColorMode { get; set; }
    [ObservableProperty] public partial string SelectedDuplex { get; set; }

    /// <summary>What the selected printer supports; unknown means: leave the option selectable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorAvailable), nameof(DuplexAvailable), nameof(ColorHint), nameof(DuplexHint))]
    public partial PrinterCapabilities Capabilities { get; set; } = PrinterCapabilities.Unknown;

    /// <summary>Colour/black and white: locked only if the printer is known to print black and white only.</summary>
    public bool ColorAvailable => Capabilities.Color != false;

    /// <summary>Duplex: not for PDF and not if the printer is known not to support it.</summary>
    public bool DuplexAvailable => !SelectedPrinter.IsPdf && Capabilities.Duplex != false;

    public string ColorHint => Capabilities.Color == false
        ? Tr.T("Dieser Drucker druckt nur Schwarzweiß.")
        : Tr.T("Schwarzweiß rechnet die Bilder selbst in Graustufen um (nach empfundener Helligkeit) — das wirkt auch im PDF und bei Druckern, die die Einstellung sonst übergehen.");

    public string DuplexHint => SelectedPrinter.IsPdf
        ? Tr.T("Beim PDF entscheidet erst das Programm, mit dem es gedruckt wird.")
        : Capabilities.Duplex == false
            ? Tr.T("Dieser Drucker kann nicht beidseitig drucken.")
            : Tr.T("Lange Kante: geblättert wird wie in einem Buch.\nKurze Kante: geblättert wird nach oben, wie bei einem Kalender oder Klemmbrett.");

    /// <summary>All open images, or only the current one.</summary>
    [ObservableProperty] public partial bool PrintAll { get; set; }

    public bool HasMultipleFiles => _main.OpenFiles.Count > 1;
    public string PrintAllText => Tr.T("Alle geöffneten Bilder ({0})", _main.OpenFiles.Count);
    public bool CopiesAvailable => !SelectedPrinter.IsPdf;

    public string PrimaryButtonText => SelectedPrinter.IsPdf ? Tr.T("PDF speichern …") : Tr.T("Drucken");

    /// <summary>Warning if the margin is smaller than the area the printer cannot print on (only where known).</summary>
    public string? MarginHint
    {
        get
        {
            if (SelectedPrinter.Printer is not { } p) return null;
            var hw = SystemPrinters.HardwareMarginMm(p.Name);
            return hw is { } min && (double)MarginMm < min
                ? Tr.T("Dieser Drucker kann am Rand {0:0.#} mm nicht bedrucken — Bilder würden angeschnitten.", min)
                : null;
        }
    }

    public bool HasMarginHint => MarginHint is not null;

    partial void OnSelectedPaperChanged(PaperSize value) => Relayout();
    partial void OnSelectedOrientationChanged(string value) => Relayout();
    partial void OnSelectedModeChanged(string value) => Relayout();
    partial void OnMarginMmChanged(decimal value) { Relayout(); OnPropertyChanged(nameof(HasMarginHint)); }
    partial void OnGapMmChanged(decimal value) => Relayout();
    partial void OnAllowRotationChanged(bool value) => Relayout();
    partial void OnShrinkToFitChanged(bool value) => Relayout();
    partial void OnPrintAllChanged(bool value) => Relayout();
    partial void OnSelectedPrinterChanged(PrinterChoice value)
    {
        OnPropertyChanged(nameof(HasMarginHint));
        Capabilities = PrinterCapabilities.Unknown;
        _ = LoadCapabilitiesAsync(value);
    }

    partial void OnCapabilitiesChanged(PrinterCapabilities value) => ApplyCapabilities();

    /// <summary>Fetches the printer list in the background; on Linux this starts lpstat.</summary>
    private async Task LoadPrintersAsync()
    {
        IReadOnlyList<PrinterInfo> found;
        try { found = await Task.Run(SystemPrinters.List); }
        catch { return; }

        var choices = found.Select(p => new PrinterChoice(p.ToString(), p)).ToList();
        foreach (var c in choices) Printers.Add(c);

        // Preselect the default printer, but only if nothing else has been selected yet.
        if (SelectedPrinter.IsPdf && choices.FirstOrDefault(c => c.Printer!.IsDefault) is { } standard)
            SelectedPrinter = standard;
    }

    /// <summary>Queries the capabilities of the printer in the background; on Linux this starts lpoptions.</summary>
    private async Task LoadCapabilitiesAsync(PrinterChoice choice)
    {
        ApplyCapabilities();
        if (choice.Printer is not { } p) return;

        PrinterCapabilities caps;
        try { caps = await Task.Run(() => SystemPrinters.Capabilities(p.Name)); }
        catch { caps = PrinterCapabilities.Unknown; }

        if (SelectedPrinter == choice)   // another printer selected in the meantime → discard
            Capabilities = caps;
    }

    /// <summary>Resets selections that are not possible: black-and-white printer, no duplex, PDF.</summary>
    private void ApplyCapabilities()
    {
        if (!ColorAvailable) SelectedColorMode = ColorModes[1];
        if (!DuplexAvailable) SelectedDuplex = DuplexModes[0];
    }

    // ------------------------------------------------------------ Preview

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(PageInfo), nameof(HasPages))]
    [NotifyCanExecuteChangedFor(nameof(PrintCommand))]
    public partial LayoutResult? Layout { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentPage), nameof(PageInfo))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand), nameof(NextPageCommand))]
    public partial int PageIndex { get; set; }

    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial bool IsPrinting { get; set; }
    [ObservableProperty] public partial string? Message { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }

    /// <summary>Thumbnails per image id, for the page preview.</summary>
    public IReadOnlyDictionary<int, Bitmap> Thumbnails =>
        _sources.Where(s => s.Thumbnail is not null).ToDictionary(s => s.Id, s => s.Thumbnail!);

    public LayoutPage? CurrentPage => Layout is { Pages.Count: > 0 } l ? l.Pages[Math.Clamp(PageIndex, 0, l.Pages.Count - 1)] : null;
    public bool HasPages => Layout is { Pages.Count: > 0 };

    public string PageInfo
    {
        get
        {
            if (IsLoading) return Tr.T("Bilder werden vorbereitet …");
            if (Layout is not { } l || l.Pages.Count == 0) return Tr.T("Nichts zu drucken");
            var images = l.Pages.Sum(p => p.Placements.Count);
            return images == 1
                ? Tr.T("Seite {0} von {1}  ·  1 Bild", PageIndex + 1, l.Pages.Count)
                : Tr.T("Seite {0} von {1}  ·  {2} Bilder", PageIndex + 1, l.Pages.Count, images);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousPage() => PageIndex--;
    private bool CanPrevious => PageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() => PageIndex++;
    private bool CanNext => Layout is { } l && PageIndex < l.Pages.Count - 1;

    /// <summary>Fetches the sizes and thumbnails of all images, then arranges them.</summary>
    public async Task LoadAsync()
    {
        var id = 1;
        foreach (var path in _main.OpenFiles)
        {
            try
            {
                // Header data and a small thumbnail only; the full image is loaded by the print job itself.
                var (w, h, dpi, thumb, tw, th) = await _main.GetListEntryAsync(path, ThumbSize);
                var state = _main.SettingsFor(path, w, h, dpi);
                var options = MainViewModel.OptionsFor(state);
                _sources.Add(new PrintSourceInfo(id++, path, options, BitmapOps.FromBgra(thumb, tw, th)));
            }
            catch (Exception ex)
            {
                Problem = Tr.T("„{0}“ ließ sich nicht lesen: {1}", Path.GetFileName(path), ex.Message);
            }
        }

        IsLoading = false;
        OnPropertyChanged(nameof(Thumbnails));
        Relayout();
    }

    private IEnumerable<PrintSourceInfo> SelectedSources =>
        PrintAll ? _sources : _sources.Where(s => s.Path == _main.CurrentImagePath);

    private LayoutOptions CurrentOptions() => new(
        SelectedPaper,
        SelectedOrientation switch { "Hochformat" => PageOrientation.Portrait, "Querformat" => PageOrientation.Landscape, _ => PageOrientation.Auto },
        (double)MarginMm,
        (double)GapMm,
        AllowRotation,
        SelectedMode switch
        {
            "Mehrere pro Seite — möglichst dicht" => PackingMode.Dense,
            "Ein Bild pro Seite" => PackingMode.OnePerPage,
            _ => PackingMode.CutFriendly,
        },
        ShrinkToFit);

    private void Relayout()
    {
        if (IsLoading) return;

        var items = SelectedSources.Select(s => PrintDocument.ToItem(s.Id, s.Options)).ToList();
        var layout = PrintLayout.Arrange(items, CurrentOptions());
        Layout = layout;
        PageIndex = Math.Clamp(PageIndex, 0, Math.Max(0, layout.Pages.Count - 1));

        var missing = layout.NotPlaced.Count;
        Problem = missing == 0 ? null
            : missing == 1
                ? Tr.T("1 Bild ist größer als die bedruckbare Fläche — „Zu große Bilder verkleinern“ einschalten oder größeres Papier wählen.")
                : Tr.T("{0} Bilder sind größer als die bedruckbare Fläche — „Zu große Bilder verkleinern“ einschalten oder größeres Papier wählen.", missing);

        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PageInfo));
    }

    // ------------------------------------------------------------ Printing

    /// <summary>Set by the view: asks for the location to save the PDF.</summary>
    public Func<string, Task<string?>>? PickPdfPath { get; set; }

    /// <summary>Set by the view: closes the window after successful printing.</summary>
    public Action? Close { get; set; }

    private bool CanPrint => !IsPrinting && HasPages;

    [RelayCommand(CanExecute = nameof(CanPrint))]
    private async Task Print()
    {
        if (Layout is not { } layout) return;

        var options = CurrentOptions();
        var sources = _sources.ToDictionary(s => s.Id);
        var title = layout.Pages.Sum(p => p.Placements.Count) == 1
            ? Path.GetFileNameWithoutExtension(sources[layout.Pages[0].Placements[0].ItemId].Path)
            : "ImageConverter";

        string? pdfPath = null;
        if (SelectedPrinter.IsPdf)
        {
            if (PickPdfPath is null) return;
            pdfPath = await PickPdfPath(title + ".pdf");
            if (pdfPath is null) return;   // cancelled
        }

        IsPrinting = true;
        Message = Tr.T("Bilder werden für den Druck gerechnet …");
        Problem = null;
        try
        {
            var printer = SelectedPrinter.Printer;
            var settings = new PrintSettings(
                (int)Copies,
                !DuplexAvailable ? DuplexMode.OneSided : SelectedDuplex == DuplexModes[1] ? DuplexMode.LongEdge
                    : SelectedDuplex == DuplexModes[2] ? DuplexMode.ShortEdge : DuplexMode.OneSided,
                SelectedColorMode == ColorModes[1]);

            // Image for a placement: from the history or freshly loaded, as for the export
            byte[] ImageFor(Placement p)
            {
                var source = sources[p.ItemId];
                var (image, owned, _, _) = _main.GetImageAsync(source.Path).GetAwaiter().GetResult();
                try { return PrintDocument.RenderForPrint(image, source.Options, p, settings.Grayscale); }
                finally { if (owned) image.Dispose(); }
            }

            var cutMarks = CutMarks;
            var gap = (double)GapMm;
            var paper = SelectedPaper;

            await Task.Run(async () =>
            {
                if (pdfPath is not null)
                {
                    await using var fs = File.Create(pdfPath);
                    PrintDocument.WritePdf(layout, ImageFor, fs, cutMarks, gap, title);
                }
                else if (OperatingSystem.IsWindows())
                {
                    SystemPrinters.PrintWithWindows(layout, ImageFor, printer!.Name, settings, paper, cutMarks, gap, title);
                }
                else
                {
                    // CUPS prints PDF directly, so first write a PDF, then send it to the printer.
                    var temp = Path.Combine(Path.GetTempPath(), $"imageconverter-{Guid.NewGuid():N}.pdf");
                    try
                    {
                        await using (var fs = File.Create(temp))
                            PrintDocument.WritePdf(layout, ImageFor, fs, cutMarks, gap, title);
                        await SystemPrinters.PrintPdfWithCupsAsync(temp, printer!.Name, settings, paper, title);
                    }
                    finally
                    {
                        // CUPS has already copied the file when it accepted the job.
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
            });

            var pages = layout.Pages.Count;
            _main.ReportPrint(pdfPath is not null
                ? (pages == 1 ? Tr.T("PDF gespeichert: {0}  ·  1 Seite", Path.GetFileName(pdfPath)) : Tr.T("PDF gespeichert: {0}  ·  {1} Seiten", Path.GetFileName(pdfPath), pages))
                : (pages == 1 ? Tr.T("An „{0}“ gesendet  ·  1 Seite", printer!.Name) : Tr.T("An „{0}“ gesendet  ·  {1} Seiten", printer!.Name, pages)), pdfPath);
            Close?.Invoke();
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
        }
        finally
        {
            IsPrinting = false;
            Message = null;
        }
    }

    partial void OnIsPrintingChanged(bool value) => PrintCommand.NotifyCanExecuteChanged();
}
