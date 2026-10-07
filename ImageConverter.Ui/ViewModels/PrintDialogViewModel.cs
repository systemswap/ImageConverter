using System;
using System.Collections.Generic;
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

/// <summary>Eintrag in der Druckerliste — „Als PDF speichern" ist ein eigener Eintrag.</summary>
public sealed record PrinterChoice(string Label, PrinterInfo? Printer)
{
    public bool IsPdf => Printer is null;
    public override string ToString() => Label;
}

/// <summary>Ein zu druckendes Bild: Druckauftrag (Größe, DPI …) und Vorschaubild.</summary>
public sealed record PrintSourceInfo(int Id, string Path, ExportOptions Options, Bitmap? Thumbnail);

/// <summary>
/// Druckdialog: Einstellungen links, Seitenvorschau rechts. Die Anordnung rechnet
/// <see cref="PrintLayout"/> bei jeder Änderung neu (Millisekunden).
/// </summary>
public partial class PrintDialogViewModel : ViewModelBase
{
    private const int ThumbSize = 320;

    private readonly MainViewModel _main;
    private readonly List<PrintSourceInfo> _sources = [];

    public PrintDialogViewModel(MainViewModel main)
    {
        _main = main;

        Printers = [new PrinterChoice("Als PDF speichern", null), ..SystemPrinters.List().Select(p => new PrinterChoice(p.ToString(), p))];
        SelectedPrinter = Printers.Skip(1).FirstOrDefault(p => p.Printer!.IsDefault) ?? Printers[0];

        Papers = PaperSize.Common;
        SelectedPaper = Papers[0];

        Orientations = ["Automatisch", "Hochformat", "Querformat"];
        SelectedOrientation = Orientations[0];

        Modes = ["Mehrere pro Seite — schneidefreundlich", "Mehrere pro Seite — möglichst dicht", "Ein Bild pro Seite"];
        SelectedMode = Modes[0];

        PrintAll = main.OpenFiles.Count > 1;
    }

    // ------------------------------------------------------------ Einstellungen

    public IReadOnlyList<PrinterChoice> Printers { get; }
    public IReadOnlyList<PaperSize> Papers { get; }
    public IReadOnlyList<string> Orientations { get; }
    public IReadOnlyList<string> Modes { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryButtonText), nameof(MarginHint), nameof(CopiesAvailable))]
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

    /// <summary>Alle geöffneten Bilder oder nur das aktuelle.</summary>
    [ObservableProperty] public partial bool PrintAll { get; set; }

    public bool HasMultipleFiles => _main.OpenFiles.Count > 1;
    public string PrintAllText => $"Alle geöffneten Bilder ({_main.OpenFiles.Count})";
    public bool CopiesAvailable => !SelectedPrinter.IsPdf;

    public string PrimaryButtonText => SelectedPrinter.IsPdf ? "PDF speichern …" : "Drucken";

    /// <summary>Warnung, wenn der Rand kleiner ist, als der Drucker bedrucken kann (nur wo bekannt).</summary>
    public string? MarginHint
    {
        get
        {
            if (SelectedPrinter.Printer is not { } p) return null;
            var hw = SystemPrinters.HardwareMarginMm(p.Name);
            return hw is { } min && (double)MarginMm < min
                ? $"Dieser Drucker kann am Rand {min:0.#} mm nicht bedrucken — Bilder würden angeschnitten."
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
    partial void OnSelectedPrinterChanged(PrinterChoice value) => OnPropertyChanged(nameof(HasMarginHint));

    // ------------------------------------------------------------ Vorschau

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

    /// <summary>Vorschaubilder je Bild-Id — für die Seitenvorschau.</summary>
    public IReadOnlyDictionary<int, Bitmap> Thumbnails =>
        _sources.Where(s => s.Thumbnail is not null).ToDictionary(s => s.Id, s => s.Thumbnail!);

    public LayoutPage? CurrentPage => Layout is { Pages.Count: > 0 } l ? l.Pages[Math.Clamp(PageIndex, 0, l.Pages.Count - 1)] : null;
    public bool HasPages => Layout is { Pages.Count: > 0 };

    public string PageInfo
    {
        get
        {
            if (IsLoading) return "Bilder werden vorbereitet …";
            if (Layout is not { } l || l.Pages.Count == 0) return "Nichts zu drucken";
            var images = l.Pages.Sum(p => p.Placements.Count);
            return $"Seite {PageIndex + 1} von {l.Pages.Count}  ·  {images} {(images == 1 ? "Bild" : "Bilder")}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanPrevious))]
    private void PreviousPage() => PageIndex--;
    private bool CanPrevious => PageIndex > 0;

    [RelayCommand(CanExecute = nameof(CanNext))]
    private void NextPage() => PageIndex++;
    private bool CanNext => Layout is { } l && PageIndex < l.Pages.Count - 1;

    /// <summary>Größen und Vorschaubilder aller Bilder holen, dann anordnen.</summary>
    public async Task LoadAsync()
    {
        var id = 1;
        foreach (var path in _main.OpenFiles)
        {
            try
            {
                var (image, owned, _) = await _main.GetImageAsync(path);
                try
                {
                    var state = _main.SettingsFor(path, (int)image.Width, (int)image.Height);
                    var options = MainViewModel.OptionsFor(state);
                    var (thumb, tw, th) = await Task.Run(() =>
                    {
                        using var t = ImageOps.CloneOf(image);
                        t.Thumbnail((uint)ThumbSize, (uint)ThumbSize);
                        return (ImageOps.ToDisplayBgra(t), (int)t.Width, (int)t.Height);
                    });
                    _sources.Add(new PrintSourceInfo(id++, path, options, BitmapOps.FromBgra(thumb, tw, th)));
                }
                finally
                {
                    if (owned) image.Dispose();
                }
            }
            catch (Exception ex)
            {
                Problem = $"„{Path.GetFileName(path)}“ ließ sich nicht lesen: {ex.Message}";
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
            : $"{missing} {(missing == 1 ? "Bild ist" : "Bilder sind")} größer als die bedruckbare Fläche — " +
              "„Zu große Bilder verkleinern“ einschalten oder größeres Papier wählen.";

        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(PageInfo));
    }

    // ------------------------------------------------------------ Drucken

    /// <summary>Von der Ansicht gesetzt: fragt nach dem Speicherort für das PDF.</summary>
    public Func<string, Task<string?>>? PickPdfPath { get; set; }

    /// <summary>Von der Ansicht gesetzt: schließt das Fenster nach erfolgreichem Druck.</summary>
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
            if (pdfPath is null) return;   // abgebrochen
        }

        IsPrinting = true;
        Message = "Bilder werden für den Druck gerechnet …";
        Problem = null;
        try
        {
            // Bild für eine Platzierung: aus dem Verlauf bzw. frisch geladen, wie beim Export
            byte[] ImageFor(Placement p)
            {
                var source = sources[p.ItemId];
                var (image, owned, _) = _main.GetImageAsync(source.Path).GetAwaiter().GetResult();
                try { return PrintDocument.RenderForPrint(image, source.Options, p); }
                finally { if (owned) image.Dispose(); }
            }

            var printer = SelectedPrinter.Printer;
            var copies = (int)Copies;
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
                    SystemPrinters.PrintWithWindows(layout, ImageFor, printer!.Name, copies, paper, cutMarks, gap, title);
                }
                else
                {
                    // CUPS druckt PDF direkt — also erst ein PDF, dann an den Drucker
                    var temp = Path.Combine(Path.GetTempPath(), $"imageconverter-{Guid.NewGuid():N}.pdf");
                    try
                    {
                        await using (var fs = File.Create(temp))
                            PrintDocument.WritePdf(layout, ImageFor, fs, cutMarks, gap, title);
                        await SystemPrinters.PrintPdfWithCupsAsync(temp, printer!.Name, copies, paper, title);
                    }
                    finally
                    {
                        // CUPS hat die Datei beim Annehmen des Auftrags bereits kopiert
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
            });

            var pages = layout.Pages.Count;
            _main.ReportPrint(pdfPath is not null
                ? $"PDF gespeichert: {Path.GetFileName(pdfPath)}  ·  {pages} {(pages == 1 ? "Seite" : "Seiten")}"
                : $"An „{printer!.Name}“ gesendet  ·  {pages} {(pages == 1 ? "Seite" : "Seiten")}", pdfPath);
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
