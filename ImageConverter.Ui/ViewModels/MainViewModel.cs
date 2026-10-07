using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageConverter.Imaging;
using ImageConverter.Models;

namespace ImageConverter.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    // Verhindert, dass das Nachrechnen der zweiten Kante das Nachrechnen
    // der ersten erneut anstoesst.
    private bool _isSyncingSize;

    // Dasselbe fuer Auswahlrahmen <-> Zahlenfelder.
    private bool _isSyncingCrop;

    private ResizeCalculator _calc = new(4000, 3000);

    public MainViewModel()
    {
        Formats = ["WebP", "PNG", "JPEG", "AVIF", "JPEG XL", "TIFF", "BMP", "ICO"];
        SelectedFormat = "WebP";

        Filters = FilterOption.All;
        SelectedFilter = Filters[0];

        MetadataModes = ["Alles behalten", "Nur GPS entfernen", "Alles entfernen"];
        SelectedMetadataMode = "Nur GPS entfernen";

        ColorModes = ["Profil behalten", "Nach sRGB wandeln", "Profil verwerfen"];
        SelectedColorMode = "Nach sRGB wandeln";

        AspectPresets = ["Frei", "Original", "1:1", "4:3", "3:2", "16:9"];
        SelectedAspectPreset = "Frei";

        // Maße des Platzhalterbilds, solange nichts geladen ist
        SourceWidth = 4000;
        SourceHeight = 3000;
        TargetWidth = 4000;
        TargetHeight = 3000;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    [NotifyCanExecuteChangedFor(nameof(RotateLeftCommand), nameof(RotateRightCommand), nameof(FlipHorizontalCommand))]
    public partial Bitmap? Preview { get; set; }

    [ObservableProperty] public partial string CurrentFileName { get; set; } = "Kein Bild geladen";
    [ObservableProperty] public partial string FileDetails { get; set; } = "Bild hierher ziehen oder „Bilder“ wählen";
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    public bool HasImage => Preview is not null;
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

        // Neue Auswahl ersetzt die alte samt Verlauf. Vorschau zuerst lösen,
        // damit nie eine freigegebene Bitmap auf dem Schirm hängt.
        Preview = null;
        DisposeAllHistories();
        _fileIndex = -1;

        _files.Clear();
        _files.AddRange(found);
        NotifyFileList();

        ErrorMessage = skipped.Count > 0
            ? $"{skipped.Count} Datei(en) übersprungen — Format wird noch nicht unterstützt."
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
            if (!_histories.TryGetValue(path, out var history))
            {
                // ImageMagick: alle Formate, EXIF-Drehung, Farbprofil → sRGB.
                // Läuft im Hintergrund, die Oberfläche bleibt bedienbar.
                var loaded = await ImageLoader.LoadAsync(path, cts.Token);
                var bitmap = BitmapOps.FromBgra(loaded.Bgra, loaded.Width, loaded.Height);

                history = new EditHistory(bitmap)
                {
                    FileBytes = loaded.FileBytes,
                    Extension = loaded.FormatName,
                };
                _histories[path] = history;
            }

            var previous = _history;
            var previousPath = _fileIndex >= 0 ? _files[_fileIndex] : null;

            _fileIndex = index;
            _history = history;
            CurrentFileName = Path.GetFileName(path);
            ShowCurrent(restore: null);
            OnPropertyChanged(nameof(FilePosition));

            // Unbearbeitete Bilder nicht im Speicher halten, bearbeitete schon —
            // sonst wäre ein Zuschnitt beim Weiterblättern verloren. Erst freigeben,
            // nachdem das neue Bild angezeigt wird.
            if (previous is { HasEdits: false } && previous != history && previousPath is not null)
            {
                _histories.Remove(previousPath);
                previous.DisposeAll();
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

    // ------------------------------------------------- Bearbeitungsverlauf

    /// <summary>Stand vor einem Bearbeitungsschritt — inklusive Rahmen, damit
    /// Rückgängig den Auswahlrahmen zum Nachjustieren zurückbringt.</summary>
    private sealed record Snapshot(Bitmap Image, Rect? Selection, double Radius);

    /// <summary>Verlauf einer Datei: aktuelles Bild plus Rückgängig/Wiederholen.</summary>
    private sealed class EditHistory(Bitmap current)
    {
        public Bitmap Current { get; set; } = current;
        public Stack<Snapshot> Undo { get; } = new();
        public Stack<Snapshot> Redo { get; } = new();
        public long FileBytes { get; init; }
        public string Extension { get; init; } = "";
        public bool HasEdits => Undo.Count > 0 || Redo.Count > 0;

        public void ClearRedo()
        {
            foreach (var s in Redo) s.Image.Dispose();
            Redo.Clear();
        }

        public void DisposeAll()
        {
            Current.Dispose();
            foreach (var s in Undo) s.Image.Dispose();
            ClearRedo();
            Undo.Clear();
        }
    }

    private readonly Dictionary<string, EditHistory> _histories = new(StringComparer.Ordinal);
    private EditHistory? _history;

    public bool CanUndo => _history?.Undo.Count > 0;
    public bool CanRedo => _history?.Redo.Count > 0;

    /// <summary>
    /// Der Ausschnitt wird zum neuen Bild. Bei runden Ecken ist alles außerhalb
    /// der Rundung danach transparent; das Bild bleibt rechteckig.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCropSelection))]
    private void CropToSelection()
    {
        if (CropSelection is not { } sel) return;
        var radius = CornerRadius;
        ApplyEdit(current => BitmapOps.Crop(current, sel, radius));
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void RotateLeft() => ApplyEdit(current => BitmapOps.Rotate90(current, clockwise: false));

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void RotateRight() => ApplyEdit(current => BitmapOps.Rotate90(current, clockwise: true));

    [RelayCommand(CanExecute = nameof(HasImage))]
    private void FlipHorizontal() => ApplyEdit(BitmapOps.FlipHorizontal);

    /// <summary>
    /// Ein Bearbeitungsschritt: Ergebnis wird das neue Bild, der vorherige Stand
    /// (inklusive Rahmen und Rundung) kommt auf den Rückgängig-Stapel.
    /// </summary>
    private void ApplyEdit(Func<Bitmap, Bitmap> operation)
    {
        if (_history is null) return;

        var result = operation(_history.Current);

        _history.Undo.Push(new Snapshot(_history.Current, CropSelection, CornerRadius));
        _history.ClearRedo();
        _history.Current = result;
        ShowCurrent(restore: null);
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void Undo()
    {
        if (_history is not { Undo.Count: > 0 } h) return;

        var previous = h.Undo.Pop();
        h.Redo.Push(new Snapshot(h.Current, null, 0));
        h.Current = previous.Image;
        ShowCurrent(restore: previous);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private void Redo()
    {
        if (_history is not { Redo.Count: > 0 } h) return;

        var next = h.Redo.Pop();
        h.Undo.Push(new Snapshot(h.Current, CropSelection, CornerRadius));
        h.Current = next.Image;
        ShowCurrent(restore: null);
    }

    /// <summary>Zeigt das aktuelle Bild des Verlaufs; stellt optional Rahmen und Rundung wieder her.</summary>
    private void ShowCurrent(Snapshot? restore)
    {
        if (_history is not { } h) return;

        Preview = h.Current;
        var px = h.Current.PixelSize;
        FileDetails = h.Undo.Count > 0
            ? $"{px.Width} × {px.Height} px  ·  {h.Extension}  ·  bearbeitet"
            : $"{px.Width} × {px.Height} px  ·  {FormatBytes(h.FileBytes)}  ·  {h.Extension}";
        ApplySourceSize(px.Width, px.Height);

        if (restore is { Selection: { } sel })
        {
            CropSelection = sel;
            CornerRadius = restore.Radius;
        }

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void DisposeAllHistories()
    {
        foreach (var h in _histories.Values) h.DisposeAll();
        _histories.Clear();
        _history = null;
    }

    private void ApplySourceSize(int width, int height)
    {
        _isSyncingSize = true;
        SourceWidth = width;
        SourceHeight = height;
        TargetWidth = width;
        TargetHeight = height;
        WidthIsLeading = true;
        _isSyncingSize = false;

        _calc = new ResizeCalculator(width, height);
        CropSelection = null;
        OnPropertyChanged(nameof(AspectRatio));
        NotifySizeDerived();
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

    // --------------------------------------------------------------- Größe
    [ObservableProperty] public partial int SourceWidth { get; set; }
    [ObservableProperty] public partial int SourceHeight { get; set; }

    [ObservableProperty] public partial FilterOption SelectedFilter { get; set; }

    [ObservableProperty] public partial bool KeepAspectRatio { get; set; } = true;
    [ObservableProperty] public partial bool DoNotEnlarge { get; set; } = true;
    [ObservableProperty] public partial bool GammaCorrect { get; set; } = true;

    [ObservableProperty] public partial int TargetWidth { get; set; }
    [ObservableProperty] public partial int TargetHeight { get; set; }

    /// <summary>Welche Kante wurde zuletzt getippt — die andere ist die abgeleitete.</summary>
    [ObservableProperty] public partial bool WidthIsLeading { get; set; } = true;

    partial void OnTargetWidthChanged(int value)
    {
        if (_isSyncingSize || !KeepAspectRatio) return;

        _isSyncingSize = true;
        WidthIsLeading = true;
        TargetHeight = _calc.HeightFor(value);
        _isSyncingSize = false;

        NotifySizeDerived();
    }

    partial void OnTargetHeightChanged(int value)
    {
        if (_isSyncingSize || !KeepAspectRatio) return;

        _isSyncingSize = true;
        WidthIsLeading = false;
        TargetWidth = _calc.WidthFor(value);
        _isSyncingSize = false;

        NotifySizeDerived();
    }

    partial void OnKeepAspectRatioChanged(bool value)
    {
        // Kette nachtraeglich geschlossen: Verhaeltnis der aktuellen Werte
        // uebernehmen, damit nichts springt.
        _calc = value
            ? new ResizeCalculator(TargetWidth, TargetHeight)
            : new ResizeCalculator(SourceWidth, SourceHeight);

        NotifySizeDerived();
    }

    private void NotifySizeDerived()
    {
        OnPropertyChanged(nameof(SizeSummary));
        OnPropertyChanged(nameof(PrintSize));
    }

    public string SizeSummary =>
        $"{SourceWidth} × {SourceHeight} → {TargetWidth} × {TargetHeight} px";

    // ---------------------------------------------------------- DPI / Druck
    [ObservableProperty] public partial int Dpi { get; set; } = 300;
    [ObservableProperty] public partial bool DpiMetadataOnly { get; set; } = true;

    partial void OnDpiChanged(int value) => OnPropertyChanged(nameof(PrintSize));

    public string PrintSize
    {
        get
        {
            if (Dpi <= 0) return "—";
            var w = TargetWidth / (double)Dpi * 2.54;
            var h = TargetHeight / (double)Dpi * 2.54;
            return $"Druckgröße {w:F1} × {h:F1} cm";
        }
    }

    // ------------------------------------------------------- Verbesserung
    [ObservableProperty] public partial double SharpenAmount { get; set; }
    [ObservableProperty] public partial double SharpenRadius { get; set; } = 1.0;
    [ObservableProperty] public partial double NoiseReduction { get; set; }
    [ObservableProperty] public partial bool UseAiDenoise { get; set; }

    public string SharpenLabel => SharpenAmount <= 0.01 ? "aus" : $"{SharpenAmount:F2}";
    public string NoiseLabel => NoiseReduction <= 0.01 ? "aus" : $"{NoiseReduction:F2}";

    partial void OnSharpenAmountChanged(double value) => OnPropertyChanged(nameof(SharpenLabel));
    partial void OnNoiseReductionChanged(double value) => OnPropertyChanged(nameof(NoiseLabel));

    // ------------------------------------------------- Metadaten und Farbe
    [ObservableProperty] public partial string SelectedMetadataMode { get; set; }
    [ObservableProperty] public partial string SelectedColorMode { get; set; }

    // ----------------------------------------------------------- Zuschnitt

    /// <summary>Auswahl in Bildpixeln; null = keine Auswahl. Kommt vom CropView.</summary>
    [ObservableProperty] public partial Rect? CropSelection { get; set; }

    [ObservableProperty] public partial string SelectedAspectPreset { get; set; }
    [ObservableProperty] public partial int CropX { get; set; }
    [ObservableProperty] public partial int CropY { get; set; }
    [ObservableProperty] public partial int CropWidth { get; set; }
    [ObservableProperty] public partial int CropHeight { get; set; }

    /// <summary>Eckenradius in Bildpixeln.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CornerRadiusLabel), nameof(ShowTransparencyWarning))]
    public partial double CornerRadius { get; set; }

    public bool HasCropSelection => CropSelection is not null;

    /// <summary>
    /// Maximum = halbe längste Seite. Am Anschlag wird die Auswahl zur Ellipse,
    /// bei einer quadratischen Auswahl zum Kreis.
    /// </summary>
    public double MaxCornerRadius =>
        CropSelection is { } s ? Math.Max(1, Math.Max(s.Width, s.Height) / 2) : 1;

    public string CornerRadiusLabel
    {
        get
        {
            if (CornerRadius < 0.5) return "eckig";
            if (CornerRadius >= MaxCornerRadius - 0.5)
                return CropWidth == CropHeight ? "Kreis" : "Ellipse";
            return $"{CornerRadius:F0} px";
        }
    }

    /// <summary>Runde Ecken werden transparent — JPEG und BMP können das nicht.</summary>
    public bool ShowTransparencyWarning =>
        CornerRadius >= 0.5 && SelectedFormat is "JPEG" or "BMP";

    public double AspectRatio => SelectedAspectPreset switch
    {
        "Original" => SourceHeight > 0 ? (double)SourceWidth / SourceHeight : 0,
        "1:1" => 1,
        "4:3" => 4.0 / 3,
        "3:2" => 3.0 / 2,
        "16:9" => 16.0 / 9,
        _ => 0,
    };

    partial void OnSelectedAspectPresetChanged(string value) => OnPropertyChanged(nameof(AspectRatio));

    partial void OnCropSelectionChanged(Rect? value)
    {
        OnPropertyChanged(nameof(HasCropSelection));
        OnPropertyChanged(nameof(MaxCornerRadius));
        CropToSelectionCommand.NotifyCanExecuteChanged();

        if (value is null)
        {
            CornerRadius = 0;
        }
        else if (CornerRadius > MaxCornerRadius)
        {
            CornerRadius = MaxCornerRadius;
        }

        if (_isSyncingCrop) return;
        _isSyncingCrop = true;
        if (value is { } r)
        {
            CropX = (int)r.X;
            CropY = (int)r.Y;
            CropWidth = (int)r.Width;
            CropHeight = (int)r.Height;
        }
        _isSyncingCrop = false;
        OnPropertyChanged(nameof(CornerRadiusLabel));
    }

    partial void OnCropXChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropYChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropWidthChanged(int value) => PushCropFieldsToSelection();
    partial void OnCropHeightChanged(int value) => PushCropFieldsToSelection();

    private void PushCropFieldsToSelection()
    {
        if (_isSyncingCrop || CropSelection is null) return;

        var x = Math.Clamp(CropX, 0, SourceWidth - 1);
        var y = Math.Clamp(CropY, 0, SourceHeight - 1);
        var w = Math.Clamp(CropWidth, 1, SourceWidth - x);
        var h = Math.Clamp(CropHeight, 1, SourceHeight - y);

        _isSyncingCrop = true;
        CropSelection = new Rect(x, y, w, h);
        _isSyncingCrop = false;
    }

    [RelayCommand]
    private void ClearSelection() => CropSelection = null;

    // ------------------------------------------------------------- Ausgabe
    [ObservableProperty] public partial string OutputFolder { get; set; } = "~/Bilder/converted";
    [ObservableProperty] public partial string NamePattern { get; set; } = "{name}_{breite}x{höhe}";
}
