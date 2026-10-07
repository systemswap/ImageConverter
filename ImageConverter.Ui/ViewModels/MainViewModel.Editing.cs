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
    /// <summary>
    /// Ein Schritt im Verlauf: der Stand <b>vor</b> der Änderung. Bei Bildänderungen
    /// (Zuschnitt, Drehen …) ist <see cref="Image"/> das Bild davor und
    /// <see cref="FilePath"/> die Datei; bei reinen Einstellungsänderungen sind beide null.
    /// </summary>
    private sealed record Snapshot(string? FilePath, MagickImage? Image, SettingsState Settings,
        bool? ApplyToAll = null, IReadOnlyDictionary<string, SettingsState>? FileSettings = null,
        SettingsState? Template = null);

    /// <summary>Das Bild einer Datei: Original und aktueller Stand.</summary>
    private sealed class EditHistory(MagickImage original)
    {
        public MagickImage Original { get; } = original;
        public MagickImage Current { get; set; } = original;
        public long FileBytes { get; init; }
        public string FormatName { get; init; } = "";
        public double Dpi { get; init; } = ImageLoader.DefaultDpi;

        /// <summary>Weicht vom geladenen Bild ab (zugeschnitten, gedreht …).</summary>
        public bool IsEdited => !ReferenceEquals(Current, Original);
    }

    // Ein gemeinsamer Verlauf für alles: Einstellungen gelten für alle Bilder,
    // Bildänderungen merken sich ihre Datei. „Rückgängig" nimmt immer den
    // letzten Schritt zurück — notfalls auch an einem anderen Bild.
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    private readonly Dictionary<string, EditHistory> _histories = new(StringComparer.Ordinal);
    private EditHistory? _history;

    // Während eines Bearbeitungsschritts (z. B. KI) nicht: das Ergebnis gehört zum Bild davor
    public bool CanUndo => !IsEditing && (_undo.Count > 0 || HasPendingSettings);
    public bool CanRedo => !IsEditing && _redo.Count > 0 && !HasPendingSettings;

    [ObservableProperty] public partial bool IsEditing { get; set; }

    partial void OnIsEditingChanged(bool value) => NotifyUndoRedo();

    private void NotifyUndoRedo()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        NotifyCompare();
    }

    /// <summary>
    /// Bild für „Vorher“: im Verlauf dieser Datei zurück, solange die Form gleich bleibt —
    /// also vor KI-Schritten, nicht aber vor einem Zuschnitt oder Drehen (andere Form).
    /// Ein Vergrößern per KI (gleiches Seitenverhältnis) zählt mit. null: kein früherer Stand.
    /// </summary>
    private MagickImage? FindBeforeImage()
    {
        if (_history is not { } h || CurrentPath is not { } path) return null;

        var before = h.Current;
        foreach (var step in _undo)   // neuester zuerst
        {
            if (step.FilePath != path || step.Image is not { } image) continue;
            if (!SameShape(image, before)) break;
            before = image;
        }

        return ReferenceEquals(before, h.Current) ? null : before;
    }

    /// <summary>Gleiches Seitenverhältnis (auf einen Pixel genau).</summary>
    private static bool SameShape(MagickImage a, MagickImage b) =>
        Math.Abs((double)a.Width * b.Height - (double)b.Width * a.Height) <= Math.Max(a.Width, b.Width);

    private void ClearRedo()
    {
        foreach (var s in _redo) s.Image?.Dispose();
        _redo.Clear();
    }

    /// <summary>Muss die Datei im Speicher bleiben, weil der Verlauf noch auf sie zeigt?</summary>
    private bool IsReferencedByHistory(string path, EditHistory h) =>
        h.IsEdited
        || HasFile(_undo, path)
        || HasFile(_redo, path);

    private static bool HasFile(Stack<Snapshot> stack, string path)
    {
        foreach (var s in stack)
            if (s.FilePath == path) return true;
        return false;
    }

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
    /// Das Ergebnis wird das neue Bild; Bild und Einstellungen davor kommen
    /// als ein Schritt auf den Rückgängig-Stapel.
    /// </summary>
    private async Task<bool> ApplyEditAsync(Func<MagickImage, MagickImage> operation)
    {
        if (_history is not { } h || IsEditing || _fileIndex < 0) return false;

        CommitPendingSettings();   // offene Reglerbewegungen sind ein eigener Schritt davor
        IsEditing = true;
        try
        {
            // Lange Schritte (KI): inzwischen kann ein anderes Bild angezeigt werden
            var path = _files[_fileIndex];
            var source = h.Current;
            var before = CaptureSettings();
            var result = await Task.Run(() => operation(source));

            await StopPreviewAsync();   // Wiederholen-Bilder könnten gerade gelesen werden
            _undo.Push(new Snapshot(path, source, before));
            ClearRedo();
            h.Current = result;
            if (h == _history) await ShowCurrentAsync();
            return true;
        }
        catch (OperationCanceledException)
        {
            // abgebrochen — Bild bleibt, wie es war
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Bearbeiten fehlgeschlagen: {ex.Message}";
            return false;
        }
        finally
        {
            IsEditing = false;
            NotifyUndoRedo();
        }
    }

    /// <summary>
    /// Für Erweiterungen (z. B. KI): ein Bearbeitungsschritt auf dem aktuellen Bild,
    /// mit Rückgängig/Wiederholen wie Zuschneiden und Drehen. Abbruch über
    /// <see cref="OperationCanceledException"/> lässt das Bild unverändert. Liefert true bei Erfolg.
    /// </summary>
    internal Task<bool> ApplyImageEditAsync(Func<MagickImage, MagickImage> operation) => ApplyEditAsync(operation);

    /// <summary>Das aktuelle Bild in voller Auflösung (mit allen Bearbeitungen) — nur lesen.</summary>
    internal MagickImage? CurrentImage => _history?.Current;

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task Undo()
    {
        CommitPendingSettings();
        if (!_undo.TryPop(out var previous)) return;

        _redo.Push(await RestoreAsync(previous));
        NotifyUndoRedo();
    }

    [RelayCommand(CanExecute = nameof(CanRedo))]
    private async Task Redo()
    {
        if (!_redo.TryPop(out var next)) return;

        _undo.Push(await RestoreAsync(next));
        NotifyUndoRedo();
    }

    /// <summary>
    /// Stellt einen Schritt wieder her und liefert sein Gegenstück für den anderen
    /// Stapel (Rückgängig ↔ Wiederholen) — gleicher Art, mit dem Stand von jetzt.
    /// </summary>
    private async Task<Snapshot> RestoreAsync(Snapshot step)
    {
        // Gehört der Schritt zu einem anderen Bild, erst dorthin wechseln
        if (step.FilePath is { } path && path != CurrentPath)
        {
            var index = _files.IndexOf(path);
            if (index >= 0) await ShowFileAsync(index);
        }

        var settingsNow = CaptureSettings();

        // Bildschritt: Bild tauschen
        MagickImage? imageNow = null;
        if (step.Image is not null && step.FilePath is not null && _histories.TryGetValue(step.FilePath, out var h))
        {
            await StopPreviewAsync();
            imageNow = h.Current;
            h.Current = step.Image;
            if (h == _history) await ShowCurrentAsync();
        }

        // Umschalten „für alle / einzeln": Modus und alle Einzelstände zurück
        bool? modeNow = null;
        SettingsState? templateNow = null;
        Dictionary<string, SettingsState>? fileSettingsNow = null;
        if (step.ApplyToAll is { } mode)
        {
            modeNow = ApplyToAll;
            templateNow = _individualTemplate;
            fileSettingsNow = new Dictionary<string, SettingsState>(_fileSettings);
            using (SuppressHistory())
                ApplyToAll = mode;
            _fileSettings = step.FileSettings is { } saved
                ? new Dictionary<string, SettingsState>(saved)
                : new Dictionary<string, SettingsState>(StringComparer.Ordinal);
            _individualTemplate = step.Template;
        }

        ApplySettings(step.Settings);
        if (!ApplyToAll && CurrentPath is { } current)
            _fileSettings[current] = CaptureSettings();

        return new Snapshot(step.FilePath, imageNow, settingsNow, modeNow, fileSettingsNow, templateNow);
    }

    /// <summary>Zeigt das aktuelle Bild der Datei; Pixelmaße beginnen wieder beim Bild.</summary>
    private async Task ShowCurrentAsync()
    {
        if (_history is not { } h) return;
        CommitPendingSettings();   // z. B. Regler bewegt, während die KI rechnete

        var doc = h.Current;
        await SetDocumentAsync(doc);

        var w = (int)doc.Width;
        var hgt = (int)doc.Height;
        FileDetails = h.IsEdited
            ? $"{w} × {hgt} px  ·  {h.FormatName}  ·  bearbeitet"
            : $"{w} × {hgt} px  ·  {FormatBytes(h.FileBytes)}  ·  {h.FormatName}";

        using (SuppressHistory())
            ApplySourceSize(w, hgt);
        ResetHistoryBaseline();

        NotifyUndoRedo();
        RequestPreview();
    }

    /// <summary>Alles freigeben — jedes Bild genau einmal, auch wenn es mehrfach referenziert ist.</summary>
    private void DisposeAllHistories()
    {
        var images = new HashSet<MagickImage>(ReferenceEqualityComparer.Instance);
        foreach (var h in _histories.Values) { images.Add(h.Original); images.Add(h.Current); }
        foreach (var s in _undo) if (s.Image is not null) images.Add(s.Image);
        foreach (var s in _redo) if (s.Image is not null) images.Add(s.Image);
        foreach (var img in images) img.Dispose();

        _histories.Clear();
        _undo.Clear();
        _redo.Clear();
        _history = null;
        _fileSettings.Clear();
        _individualTemplate = null;
        _pendingBaseline = null;
        NotifyUndoRedo();
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
