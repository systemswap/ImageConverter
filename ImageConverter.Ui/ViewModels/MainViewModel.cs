using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

/// <summary>
/// Hauptansicht. Aufgeteilt auf mehrere Dateien:
/// <list type="bullet">
/// <item>MainViewModel.cs — Dateien, Format, Metadaten, Ausgabe</item>
/// <item>MainViewModel.Editing.cs — Zuschneiden, Drehen, Rückgängig/Wiederholen</item>
/// <item>MainViewModel.Size.cs — Pixelmaße, Kette, DPI, Druckgröße</item>
/// <item>MainViewModel.Preview.cs — Live-Vorschau für Skalieren, Entrauschen, Schärfen</item>
/// </list>
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        Formats = ["WebP", "PNG", "JPEG", "AVIF", "JPEG XL", "TIFF", "BMP", "ICO"];
        SelectedFormat = "WebP";

        Filters = FilterOption.All;
        SelectedFilter = Filters[0];

        MetadataModes = ["Alles behalten", "Nur GPS entfernen", "Alles entfernen"];
        SelectedMetadataMode = "Alles behalten";

        ColorModes = ["Profil behalten", "Nach sRGB wandeln", "Profil verwerfen"];
        SelectedColorMode = "Nach sRGB wandeln";

        AspectPresets = ["Frei", "Original", "1:1", "4:3", "3:2", "16:9"];
        SelectedAspectPreset = "Frei";

        // Maße des Platzhalterbilds, solange nichts geladen ist
        ApplySourceSize(4000, 3000);
    }

    public ObservableCollection<string> Formats { get; }
    public IReadOnlyList<FilterOption> Filters { get; }
    public ObservableCollection<string> MetadataModes { get; }
    public ObservableCollection<string> ColorModes { get; }
    public ObservableCollection<string> AspectPresets { get; }

    // ------------------------------------------------------------- Dateien

    private readonly List<string> _files = [];
    private int _fileIndex = -1;

    // Bricht ein noch laufendes Laden ab, wenn schon das nächste Bild gewünscht ist.
    private CancellationTokenSource? _loadCts;

    /// <summary>Kurzliste für den leeren Zustand — nur, was ImageMagick hier wirklich liest.</summary>
    public string SupportedFormatsSummary
    {
        get
        {
            (string Label, string Ext)[] headline =
            [
                ("JPEG", ".jpg"), ("PNG", ".png"), ("WebP", ".webp"), ("AVIF", ".avif"),
                ("HEIC", ".heic"), ("JPEG XL", ".jxl"), ("TIFF", ".tif"), ("GIF", ".gif"), ("RAW", ".dng"),
            ];
            return string.Join(" · ", headline
                .Where(h => ImageFormats.ReadableExtensions.Contains(h.Ext))
                .Select(h => h.Label)) + " …";
        }
    }

    [ObservableProperty] public partial string CurrentFileName { get; set; } = "Kein Bild geladen";
    [ObservableProperty] public partial string FileDetails { get; set; } = "Bild hierher ziehen oder „Bilder“ wählen";
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public int FileCount => _files.Count;
    public bool HasMultipleFiles => _files.Count > 1;
    public string FilePosition => _files.Count > 1 ? $"{_fileIndex + 1} / {_files.Count}" : "";

    /// <summary>
    /// Übernimmt Dateien und Ordner (aus Drag &amp; Drop, Dateidialog oder
    /// Kommandozeile) und zeigt das erste Bild. Ordner werden flach durchsucht.
    /// </summary>
    public async Task OpenAsync(IEnumerable<string> paths)
    {
        var found = new List<string>();
        var skipped = new List<string>();

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                found.AddRange(Directory.EnumerateFiles(path)
                    .Where(ImageFormats.CanRead)
                    .Order(StringComparer.CurrentCultureIgnoreCase));
            }
            else if (File.Exists(path))
            {
                if (ImageFormats.CanRead(path)) found.Add(path);
                else skipped.Add(Path.GetFileName(path));
            }
        }

        if (found.Count == 0)
        {
            ErrorMessage = skipped.Count > 0
                ? $"„{skipped[0]}“ ist kein unterstütztes Bildformat."
                : "Keine Bilder gefunden.";
            return;
        }

        // Neue Auswahl ersetzt die alte samt Verlauf. Erst die Vorschau-Berechnung
        // abwarten — sie liest evtl. noch ein Bild, das gleich freigegeben wird.
        await StopPreviewAsync();
        ClearDisplay();
        DisposeAllHistories();
        _fileIndex = -1;

        _files.Clear();
        _files.AddRange(found);
        NotifyFileList();

        ErrorMessage = skipped.Count > 0
            ? $"{skipped.Count} Datei(en) übersprungen — Format wird nicht unterstützt."
            : null;

        await ShowFileAsync(0);
    }

    [RelayCommand(CanExecute = nameof(HasMultipleFiles))]
    private Task PreviousFile() => ShowFileAsync((_fileIndex - 1 + _files.Count) % _files.Count);

    [RelayCommand(CanExecute = nameof(HasMultipleFiles))]
    private Task NextFile() => ShowFileAsync((_fileIndex + 1) % _files.Count);

    private async Task ShowFileAsync(int index)
    {
        var path = _files[index];

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();

        IsLoading = true;
        try
        {
            var isNew = false;
            if (!_histories.TryGetValue(path, out var history))
            {
                // ImageMagick: alle Formate, EXIF-Drehung, Farbprofil → sRGB.
                // Läuft im Hintergrund, die Oberfläche bleibt bedienbar.
                var loaded = await ImageLoader.LoadAsync(path, cts.Token);
                history = new EditHistory(loaded.Image)
                {
                    FileBytes = loaded.FileBytes,
                    FormatName = loaded.FormatName,
                    Dpi = loaded.Dpi,
                };
                _histories[path] = history;
                isNew = true;
            }

            var previous = _history;
            var previousPath = _fileIndex >= 0 ? _files[_fileIndex] : null;

            _fileIndex = index;
            _history = history;
            CurrentFileName = Path.GetFileName(path);
            OnPropertyChanged(nameof(FilePosition));

            // DPI aus der Datei übernehmen — aber nur beim ersten Öffnen, danach
            // gilt, was eingestellt wurde.
            if (isNew) SetDpiSilently((int)Math.Round(history.Dpi));

            await ShowCurrentAsync(restore: null);

            // Unbearbeitete Bilder nicht im Speicher halten, bearbeitete schon —
            // sonst wäre ein Zuschnitt beim Weiterblättern verloren.
            if (previous is { HasEdits: false } && previous != history && previousPath is not null)
            {
                await StopPreviewAsync();
                _histories.Remove(previousPath);
                previous.DisposeAll();
                RequestPreview();
            }
        }
        catch (OperationCanceledException)
        {
            // Inzwischen wurde ein anderes Bild gewählt — kein Fehler
        }
        catch (Exception ex)
        {
            ErrorMessage = $"„{Path.GetFileName(path)}“ ließ sich nicht öffnen: {ex.Message}";
        }
        finally
        {
            if (_loadCts == cts) IsLoading = false;
        }
    }

    private void NotifyFileList()
    {
        OnPropertyChanged(nameof(FileCount));
        OnPropertyChanged(nameof(HasMultipleFiles));
        OnPropertyChanged(nameof(FilePosition));
        PreviousFileCommand.NotifyCanExecuteChanged();
        NextFileCommand.NotifyCanExecuteChanged();
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };

    // -------------------------------------------------------------- Format
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTransparencyWarning))]
    public partial string SelectedFormat { get; set; }

    [ObservableProperty] public partial double Quality { get; set; } = 82;
    [ObservableProperty] public partial bool Lossless { get; set; }

    public string EstimatedSize => $"≈ {Math.Max(12, (int)(Quality * 5.2))} KB";
    public string SavingsText => $"{Math.Clamp(100 - (int)(Quality * 0.9), 5, 95)} % kleiner als das Original";

    partial void OnQualityChanged(double value)
    {
        OnPropertyChanged(nameof(EstimatedSize));
        OnPropertyChanged(nameof(SavingsText));
    }

    // ------------------------------------------------- Metadaten und Farbe
    [ObservableProperty] public partial string SelectedMetadataMode { get; set; }
    [ObservableProperty] public partial string SelectedColorMode { get; set; }

    // ------------------------------------------------------------- Ausgabe
    [ObservableProperty] public partial string OutputFolder { get; set; } = "~/Bilder/converted";
    [ObservableProperty] public partial string NamePattern { get; set; } = "{name}_{breite}x{höhe}";
}
