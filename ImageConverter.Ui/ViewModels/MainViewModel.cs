using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Ai;
using ImageConverter.Core.Imaging;
using ImageConverter.Core.Localization;
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

        // Ausgangszustand für neue Bilder (siehe ResetToStart)
        _startSettings = CaptureSettings();

        // Ab hier zählen Änderungen als Rückgängig-Schritte
        ResetHistoryBaseline();

        // KI-Modul (abtrennbar, siehe Ordner Ai/)
        Ai = new AiViewModel(this);

        // Sprachwechsel: berechnete Texte (Hinweise, Druckgröße …) neu abfragen lassen
        Tr.Instance.PropertyChanged += (_, _) => RefreshTexts();
    }

    /// <summary>KI-Verbesserung — eigenes Modul mit GroupBox und Einstellungs-Tab.</summary>
    public AiViewModel Ai { get; }

    /// <summary>Tab „Sprache“ in den Einstellungen.</summary>
    public LanguageViewModel Language { get; } = new();

    /// <summary>Nach einem Sprachwechsel: alle Bindungen neu lesen, gemerkte Texte neu bilden.</summary>
    private void RefreshTexts()
    {
        OnPropertyChanged(string.Empty);
        if (_history is null)
        {
            CurrentFileName = Tr.T("Kein Bild geladen");
            FileDetails = Tr.T("Bild hierher ziehen oder „Bilder“ wählen");
        }
        UpdateFileDetails();
        RequestEstimate();
    }

    /// <summary>Von der Ansicht abonniert: öffnet das Einstellungsfenster.</summary>
    public event Func<Task>? SettingsRequested;

    [RelayCommand]
    private Task OpenSettings() => SettingsRequested?.Invoke() ?? Task.CompletedTask;

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

    [ObservableProperty] public partial string CurrentFileName { get; set; } = Tr.T("Kein Bild geladen");
    [ObservableProperty] public partial string FileDetails { get; set; } = Tr.T("Bild hierher ziehen oder „Bilder“ wählen");
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public int FileCount => _files.Count;
    public bool HasMultipleFiles => _files.Count > 1;
    public string FilePosition => _files.Count > 1 ? $"{_fileIndex + 1} / {_files.Count}" : "";

    /// <summary>Alle Einstellungen, wie sie beim Programmstart sind (gespeichert wird nur die Sprache).</summary>
    private readonly SettingsState _startSettings;

    /// <summary>Wird ausgelöst, wenn neue Bilder die bisherigen ersetzen — Erweiterungen (KI) beginnen dann auch neu.</summary>
    internal event EventHandler? FilesReplaced;

    /// <summary>
    /// Neu geladene Bilder beginnen wie nach dem Programmstart: alle Einstellungen (Format, Qualität, Größe =
    /// Originalauflösung, DPI aus der Datei, Licht, Dunst, Farbe, Schärfe …) auf den Vorgaben, gleiche
    /// Einstellungen für alle. Kein Rückgängig-Schritt — der Verlauf beginnt mit den neuen Bildern neu.
    /// Blättern zwischen schon geöffneten Bildern ändert nichts.
    /// </summary>
    internal void ResetToStart()
    {
        using (SuppressHistory())
        {
            ApplyToAll = true;
            ApplySettings(_startSettings);
        }
        SelectionTool = true;
        ResetHistoryBaseline();
        FilesReplaced?.Invoke(this, EventArgs.Empty);
    }

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
                ? Tr.T("„{0}“ ist kein unterstütztes Bildformat.", skipped[0])
                : Tr.T("Keine Bilder gefunden.");
            return;
        }

        // Neue Auswahl ersetzt die alte samt Verlauf. Erst die Vorschau-Berechnung
        // abwarten — sie liest evtl. noch ein Bild, das gleich freigegeben wird.
        await StopPreviewAsync();
        ClearDisplay();
        DisposeAllHistories();
        ResetToStart();   // neue Bilder beginnen wie nach dem Programmstart
        _fileIndex = -1;

        _files.Clear();
        _files.AddRange(found);
        NotifyFileList();

        ErrorMessage = skipped.Count > 0
            ? Tr.T("{0} Datei(en) übersprungen — Format wird nicht unterstützt.", skipped.Count)
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
        CommitPendingSettings();   // offene Reglerbewegungen gehören noch zum vorigen Bild

        // Stand des Bildes, das gerade verlassen wird. Im Einzelmodus merken;
        // im gemeinsamen Modus ist er die Vorlage für das nächste Bild.
        var outgoing = _history is not null ? CaptureSettings() : null;
        var outgoingFileDpi = _history?.Dpi;
        if (!ApplyToAll && CurrentPath is { } leaving && outgoing is not null)
            _fileSettings[leaving] = outgoing;

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

            await ShowCurrentAsync();

            // Einstellungen für dieses Bild: eigener Stand (Einzelmodus) oder der
            // bisherige Stand, auf dieses Bild umgerechnet.
            if (!ApplyToAll && _fileSettings.TryGetValue(path, out var own))
            {
                ApplySettings(own);
            }
            else if (outgoing is not null)
            {
                // DPI folgt der Datei, solange man sie beim vorigen Bild nicht selbst
                // geändert hatte — sonst gilt die eigene Einstellung für alle.
                // Einzelmodus ohne eigenen Stand: Vorlage vom Umschalten, nicht das vorige Bild
                var template = !ApplyToAll && _individualTemplate is { } t ? t : outgoing;
                if (isNew && outgoingFileDpi is { } prevDpi && template.Dpi == (int)Math.Round(prevDpi))
                    template = template with { Dpi = (int)Math.Round(history.Dpi) };
                ApplySettings(AdaptToImage(template, SourceWidth, SourceHeight));
            }
            else if (isNew)
            {
                // Allererstes Bild: DPI aus der Datei
                using (SuppressHistory())
                    SetDpiSilently((int)Math.Round(history.Dpi));
                ResetHistoryBaseline();
            }

            // Unbearbeitete Bilder nicht im Speicher halten, bearbeitete schon —
            // sonst wäre ein Zuschnitt beim Weiterblättern verloren.
            if (previous is not null && previous != history && previousPath is not null
                && !IsReferencedByHistory(previousPath, previous))
            {
                await StopPreviewAsync();
                _histories.Remove(previousPath);
                previous.Current.Dispose();   // unbearbeitet: Current ist das Original
                RequestPreview();
            }
        }
        catch (OperationCanceledException)
        {
            // Inzwischen wurde ein anderes Bild gewählt — kein Fehler
        }
        catch (Exception ex)
        {
            ErrorMessage = Tr.T("„{0}“ ließ sich nicht öffnen: {1}", Path.GetFileName(path), ex.Message);
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
}
