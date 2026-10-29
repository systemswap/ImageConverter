using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Ai;
using ImageConverter.ViewModels;
using ImageMagick;

namespace ImageConverter.Ai;

/// <summary>
/// "Entfernen" (remove) tab: the brush marks what is to disappear (watermarks, text, distracting objects), then a model
/// (LaMa, MI-GAN) fills these areas to match their surroundings. Each removal is an undoable step.
/// </summary>
/// <remarks>
/// The mark remains after a removal: a new attempt (different model, extended mark) starts again from the image before
/// the removal instead of building on the first result. Only "Neue Markierung" (new mark) starts from the current image.
/// </remarks>
public partial class RetouchViewModel : ViewModelBase, IBrushTool
{
    private readonly MainViewModel _main;
    private readonly AiViewModel _ai;
    private CancellationTokenSource? _runCts;

    public RetouchViewModel(MainViewModel main, AiViewModel ai)
    {
        _main = main;
        _ai = ai;
        _ai.ModelsChanged += (_, _) => Rescan();
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.FilesReplaced += (_, _) => ClearAll();
        Rescan();
    }

    // ------------------------------------------------------------ Models

    /// <summary>Installed inpainting models.</summary>
    public ObservableCollection<AiModel> Available { get; } = [];

    public bool HasModels => Available.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelHint))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial AiModel? SelectedModel { get; set; }

    public string? ModelHint => SelectedModel is { } m ? Tr.T(m.Description) : null;

    private void Rescan()
    {
        var usable = AiModelCatalog.All.Where(m => m.Kind == AiModelKind.Inpaint && _ai.Store.IsInstalled(m)).ToList();
        var selected = SelectedModel;
        Available.Clear();
        foreach (var m in usable) Available.Add(m);
        SelectedModel = selected is not null && usable.Contains(selected) ? selected : usable.FirstOrDefault();
        OnPropertyChanged(nameof(HasModels));
    }

    /// <summary>Opens the settings on the "Entfernen" page for downloading models.</summary>
    [RelayCommand]
    private Task OpenModels() => _main.OpenSettingsCommand.ExecuteAsync(SettingsPage.Remove);

    // ------------------------------------------------------------ Brush

    /// <summary>Whether the "Entfernen" tab is selected; the mouse then paints in the preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushing))]
    public partial bool IsTabActive { get; set; }

    partial void OnIsTabActiveChanged(bool value)
    {
        if (!value) return;
        // Painting happens on the unrotated image: close the rotation, perspective and dewarp tools and the eyedropper.
        _main.RotationTool = false;
        _main.PerspectiveTool = false;
        _main.DewarpTool = false;
        _main.IsPicking = false;
    }

    /// <summary>Whether the brush is active: tab selected, an image loaded, no computation running.</summary>
    public bool IsBrushing
    {
        get => IsTabActive && _main.HasImage && !IsRunning;
        set { }   // Esc in the preview does not end the brush; the tab does that
    }

    /// <summary>Brush size as a diameter in screen pixels, so the brush keeps its on-screen size when zooming.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushSizeLabel))]
    public partial double BrushSize { get; set; } = 40;

    public string BrushSizeLabel => $"{BrushSize:0} px";

    /// <summary>Removal paints only red.</summary>
    public bool BrushKeep => false;

    System.Windows.Input.ICommand IBrushTool.AddStrokeCommand => AddStrokeCommand;

    /// <summary>The mark: all strokes, oldest first (a new list on every change so that the preview updates).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStrokes))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(UndoStrokeCommand), nameof(ClearStrokesCommand))]
    public partial IReadOnlyList<BrushStroke> Strokes { get; set; } = [];

    public bool HasStrokes => Strokes.Count > 0;

    /// <summary>The mark has already been removed; it is hidden in the preview (so the result is unobstructed) until something is added.</summary>
    [ObservableProperty] public partial bool StrokesApplied { get; set; }

    /// <summary>Adds a stroke from the preview (mouse released).</summary>
    [RelayCommand]
    private void AddStroke(BrushStroke stroke)
    {
        Strokes = [.. Strokes, stroke];
        StrokesApplied = false;
        Status = null;
    }

    private bool CanEditStrokes => HasStrokes && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanEditStrokes))]
    private void UndoStroke()
    {
        Strokes = Strokes.Take(Strokes.Count - 1).ToArray();
        StrokesApplied = false;
    }

    /// <summary>Clears the mark; the next removal builds on the current image.</summary>
    [RelayCommand(CanExecute = nameof(CanEditStrokes))]
    private void ClearStrokes()
    {
        Strokes = [];
        StrokesApplied = false;
        _last = null;
        Status = null;
    }

    // ------------------------------------------------------------ Removal

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushing))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(CancelCommand), nameof(UndoStrokeCommand), nameof(ClearStrokesCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>
    /// Last removal: its result and the image before it. If the result is still the current image and the mark has not
    /// been cleared, a new attempt starts from the image before, so that nothing accumulates.
    /// </summary>
    private (MagickImage Result, MagickImage Base)? _last;

    private bool CanRemove => HasStrokes && SelectedModel is not null && !IsRunning && _main.HasImage && !_main.IsEditing;

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private async Task Remove()
    {
        if (SelectedModel is not { } model || _main.CurrentImage is not { } current) return;
        if (_ai.Store.PathOf(model) is not { } path)
        {
            Status = Tr.T("Modell fehlt — bitte in den Einstellungen herunterladen.");
            return;
        }

        var again = _last is { } last && ReferenceEquals(current, last.Result);
        var start = again ? _last!.Value.Base : current;
        // The strokes were painted on the displayed image (rotated, cropped …); filling happens in the pixel image.
        var strokes = Strokes.Select(ToPixels).ToArray();
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        AppLog.Activity($"Entfernen: {model.Name}");
        var clock = Stopwatch.StartNew();
        Status = Tr.T("Wird entfernt …");
        _ai.ShowActivity(Tr.T("Entfernen · Modell wird geladen …"), 0, CancelCommand);
        var progress = new Progress<double>(f => _ai.ShowActivity(Tr.T("Entfernen · {0:0} %", f * 100), f * 100, CancelCommand));
        try
        {
            var result = await _main.ApplyRetouchAsync(_ =>
            {
                var session = _ai.Sessions.Get(model, path);
                return Inpainter.Run(start, strokes, new OnnxInpaintModel(session, model), progress, cts.Token);
            }, Tr.T(model.Name), again);

            if (result is not null)
            {
                _last = (result, start);
                StrokesApplied = true;
                Status = Tr.T("Entfernt nach {0:0.0} s — rückgängig mit Strg+Z. Anderes Modell oder Markierung ergänzt: noch einmal „Entfernen“ rechnet wieder vom Bild davor.",
                    clock.Elapsed.TotalSeconds);
            }
            else
            {
                Status = cts.IsCancellationRequested ? Tr.T("Abgebrochen.") : Tr.T("Fehlgeschlagen — siehe Meldung oben.");
            }
        }
        catch (Exception ex)
        {
            Status = Tr.T("Fehler: {0}", ex.Message);
        }
        finally
        {
            _runCts = null;
            IsRunning = false;
            AppLog.Activity(null);
            _ai.HideActivity();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>Maps a stroke from the displayed image to the pixel image: points and radius (the radius varies by position when the image is dewarped).</summary>
    private BrushStroke ToPixels(BrushStroke s)
    {
        var points = s.Points.Select(p => _main.ShownToPixels(p.X, p.Y)).ToArray();
        var radius = s.Points.Count == 0 ? s.Radius : s.Points.Average(p => _main.ShownLengthToPixels(p.X, p.Y, s.Radius));
        return new BrushStroke(points, radius);
    }

    // ------------------------------------------------------------ Image change

    private void ClearAll()
    {
        Strokes = [];
        StrokesApplied = false;
        _last = null;
        Status = null;
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.HasImage):
                OnPropertyChanged(nameof(IsBrushing));
                RemoveCommand.NotifyCanExecuteChanged();
                break;
            case nameof(MainViewModel.IsEditing):
                RemoveCommand.NotifyCanExecuteChanged();
                break;
            case nameof(MainViewModel.CurrentFileName):
                // different image: the mark belongs to the previous one
                if (!IsRunning) ClearAll();
                break;
            case nameof(MainViewModel.DocumentSize):
                // Displayed dimensions changed (crop, rotation, tool view, or undoing them): the mark lies in the
                // displayed image and no longer fits.
                var now = (_main.DocumentSize.Width, _main.DocumentSize.Height);
                if (!IsRunning && _size is { } size && size != now) ClearAll();
                _size = now;
                break;
        }
    }

    private (int W, int H)? _size;
}
