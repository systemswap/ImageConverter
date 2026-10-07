using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Imaging;
using ImageMagick;

namespace ImageConverter.ViewModels;

// Zuschneiden, Drehen, Spiegeln und der Verlauf dafür.
public partial class MainViewModel
{
    /// <summary>Stand vor einem Bearbeitungsschritt — inklusive Rahmen, damit
    /// Rückgängig den Auswahlrahmen zum Nachjustieren zurückbringt.</summary>
    private sealed record Snapshot(MagickImage Image, Rect? Selection, double Radius);

    /// <summary>Verlauf einer Datei: aktuelles Bild plus Rückgängig/Wiederholen.</summary>
    private sealed class EditHistory(MagickImage current)
    {
        public MagickImage Current { get; set; } = current;
        public Stack<Snapshot> Undo { get; } = new();
        public Stack<Snapshot> Redo { get; } = new();
        public long FileBytes { get; init; }
        public string FormatName { get; init; } = "";
        public double Dpi { get; init; } = ImageLoader.DefaultDpi;
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
            Undo.Clear();
            ClearRedo();
        }
    }

    private readonly Dictionary<string, EditHistory> _histories = new(StringComparer.Ordinal);
    private EditHistory? _history;

    public bool CanUndo => _history?.Undo.Count > 0;
    public bool CanRedo => _history?.Redo.Count > 0;

    [ObservableProperty] public partial bool IsEditing { get; set; }

    /// <summary>
    /// Der Ausschnitt wird zum neuen Bild. Bei runden Ecken ist alles außerhalb
    /// der Rundung danach transparent; das Bild bleibt rechteckig.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasCropSelection))]
    private Task CropToSelection()
    {
        if (CropSelection is not { } sel) return Task.CompletedTask;
        var radius = CornerRadius;
        return ApplyEditAsync(img => ImageOps.Crop(img,
            (int)Math.Round(sel.X), (int)Math.Round(sel.Y),
            (int)Math.Round(sel.Width), (int)Math.Round(sel.Height), radius));
    }

    [RelayCommand(CanExecute = nameof(HasImage))]
    private Task RotateLeft() => ApplyEditAsync(img => ImageOps.Rotate90(img, clockwise: false));

    [RelayCommand(CanExecute = nameof(HasImage))]
    private Task RotateRight() => ApplyEditAsync(img => ImageOps.Rotate90(img, clockwise: true));

    [RelayCommand(CanExecute = nameof(HasImage))]
    private Task FlipHorizontal() => ApplyEditAsync(ImageOps.FlipHorizontal);

    /// <summary>
    /// Ein Bearbeitungsschritt auf dem echten Bild (ImageMagick, im Hintergrund).
    /// Das Ergebnis wird das neue Bild, der vorherige Stand inklusive Rahmen und
    /// Rundung kommt auf den Rückgängig-Stapel.
    /// </summary>
    private async Task ApplyEditAsync(Func<MagickImage, MagickImage> operation)
    {
        if (_history is not { } h || IsEditing) return;

        IsEditing = true;
        try
        {
            var source = h.Current;
            var result = await Task.Run(() => operation(source));

            h.Undo.Push(new Snapshot(source, CropSelection, CornerRadius));
            await StopPreviewAsync();   // Wiederholen-Bilder könnten gerade gelesen werden
            h.ClearRedo();
            h.Current = result;
            await ShowCurrentAsync(restore: null);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bearbeiten fehlgeschlagen: {ex.Message}";
        }
        finally
        {
            IsEditing = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task Undo()
    {
        if (_history is not { Undo.Count: > 0 } h) return;

        var previous = h.Undo.Pop();
        h.Redo.Push(new Snapshot(h.Current, null, 0));
        h.Current = previous.Image;
        await ShowCurrentAsync(restore: previous);
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task Redo()
    {
        if (_history is not { Redo.Count: > 0 } h) return;

        var next = h.Redo.Pop();
        h.Undo.Push(new Snapshot(h.Current, CropSelection, CornerRadius));
        h.Current = next.Image;
        await ShowCurrentAsync(restore: null);
    }

    /// <summary>Zeigt das aktuelle Bild des Verlaufs; stellt optional Rahmen und Rundung wieder her.</summary>
    private async Task ShowCurrentAsync(Snapshot? restore)
    {
        if (_history is not { } h) return;

        var doc = h.Current;
        await SetDocumentAsync(doc);

        var w = (int)doc.Width;
        var hgt = (int)doc.Height;
        FileDetails = h.Undo.Count > 0
            ? $"{w} × {hgt} px  ·  {h.FormatName}  ·  bearbeitet"
            : $"{w} × {hgt} px  ·  {FormatBytes(h.FileBytes)}  ·  {h.FormatName}";
        ApplySourceSize(w, hgt);

        if (restore is { Selection: { } sel })
        {
            CropSelection = sel;
            CornerRadius = restore.Radius;
        }

        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();

        RequestPreview();
    }

    private void DisposeAllHistories()
    {
        foreach (var h in _histories.Values) h.DisposeAll();
        _histories.Clear();
        _history = null;
    }

    // ----------------------------------------------------------- Zuschnitt

    /// <summary>Auswahl in Bildpixeln; null = keine Auswahl. Kommt vom CropView.</summary>
    [ObservableProperty] public partial Rect? CropSelection { get; set; }

    [ObservableProperty] public partial string SelectedAspectPreset { get; set; }
    [ObservableProperty] public partial int CropX { get; set; }
    [ObservableProperty] public partial int CropY { get; set; }
    [ObservableProperty] public partial int CropWidth { get; set; }
    [ObservableProperty] public partial int CropHeight { get; set; }

    // Verhindert Ping-Pong zwischen Auswahlrahmen und Zahlenfeldern
    private bool _isSyncingCrop;

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
            CornerRadius = 0;
        else if (CornerRadius > MaxCornerRadius)
            CornerRadius = MaxCornerRadius;

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
}
