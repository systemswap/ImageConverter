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
/// Tab „Entfernen“: mit dem Pinsel markieren, was verschwinden soll (Wasserzeichen, Schrift, Störendes), dann füllt
/// ein Modell (LaMa, MI-GAN) die Stellen passend zur Umgebung. Jedes Entfernen ist ein Schritt mit Rückgängig.
/// </summary>
/// <remarks>
/// Die Markierung bleibt nach dem Entfernen stehen: Ein neuer Versuch (anderes Modell, Markierung ergänzt) rechnet
/// wieder vom Bild davor, statt auf dem ersten Ergebnis aufzubauen. Erst „Neue Markierung“ beginnt beim aktuellen Bild.
/// </remarks>
public partial class RetouchViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly AiViewModel _ai;
    private CancellationTokenSource? _runCts;

    public RetouchViewModel(MainViewModel main, AiViewModel ai)
    {
        _main = main;
        _ai = ai;
        _ai.ModelsChanged += (_, _) => Rescan();
        _ai.BusyElsewhere = () => IsRunning;
        _main.PropertyChanged += OnMainPropertyChanged;
        _main.FilesReplaced += (_, _) => ClearAll();
        Rescan();
    }

    // ------------------------------------------------------------ Modelle

    /// <summary>Vorhandene Füllmodelle.</summary>
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

    /// <summary>Öffnet die Einstellungen (Tab „KI-Modelle“) zum Herunterladen.</summary>
    [RelayCommand]
    private Task OpenModels() => _main.OpenSettingsCommand.ExecuteAsync(null);

    // ------------------------------------------------------------ Pinsel

    /// <summary>Tab „Entfernen“ ist gewählt — dann malt die Maus in der Vorschau.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushing))]
    public partial bool IsTabActive { get; set; }

    partial void OnIsTabActiveChanged(bool value)
    {
        if (!value) return;
        // Gemalt wird auf dem ungedrehten Bild — Drehen, Entzerren und Pipette schließen
        _main.RotationTool = false;
        _main.PerspectiveTool = false;
        _main.IsPicking = false;
    }

    /// <summary>Pinsel aktiv: Tab gewählt, ein Bild da, nichts rechnet.</summary>
    public bool IsBrushing
    {
        get => IsTabActive && _main.HasImage && !IsRunning;
        set { }   // Esc in der Vorschau beendet den Pinsel nicht — dafür gibt es den Tab
    }

    /// <summary>Pinselgröße als Durchmesser in Bildschirmpixeln — beim Zoomen bleibt der Pinsel gleich groß.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BrushSizeLabel))]
    public partial double BrushSize { get; set; } = 40;

    public string BrushSizeLabel => $"{BrushSize:0} px";

    /// <summary>Die Markierung: alle Striche, älteste zuerst (für die Vorschau als neue Liste je Änderung).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStrokes))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(UndoStrokeCommand), nameof(ClearStrokesCommand))]
    public partial IReadOnlyList<BrushStroke> Strokes { get; set; } = [];

    public bool HasStrokes => Strokes.Count > 0;

    /// <summary>Markierung schon entfernt — in der Vorschau ausgeblendet (das Ergebnis soll frei zu sehen sein), bis etwas dazukommt.</summary>
    [ObservableProperty] public partial bool StrokesApplied { get; set; }

    /// <summary>Ein Strich aus der Vorschau (Maus losgelassen).</summary>
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

    /// <summary>Markierung löschen — das nächste Entfernen baut auf dem aktuellen Bild auf.</summary>
    [RelayCommand(CanExecute = nameof(CanEditStrokes))]
    private void ClearStrokes()
    {
        Strokes = [];
        StrokesApplied = false;
        _last = null;
        Status = null;
    }

    // ------------------------------------------------------------ Entfernen

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrushing))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand), nameof(CancelCommand), nameof(UndoStrokeCommand), nameof(ClearStrokesCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>
    /// Letztes Entfernen: Ergebnis und Bild davor. Ist das Ergebnis noch das aktuelle Bild und die Markierung nicht
    /// gelöscht, rechnet ein neuer Versuch vom Bild davor — nichts häuft sich an.
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
        // Gemalt wurde im angezeigten Bild (gedreht, zugeschnitten …) — gefüllt wird im Pixelbild
        var strokes = Strokes.Select(ToPixels).ToArray();
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        var clock = Stopwatch.StartNew();
        Status = Tr.T("Wird entfernt …");
        _ai.ShowActivity(Tr.T("Entfernen · Modell wird geladen …"), 0, CancelCommand);
        var progress = new Progress<double>(f => _ai.ShowActivity(Tr.T("Entfernen · {0:0} %", f * 100), f * 100, CancelCommand));
        try
        {
            var ok = await _main.ApplyRetouchAsync(_ =>
            {
                var session = _ai.Sessions.Get(model, path);
                return Inpainter.Run(start, strokes, new OnnxInpaintModel(session, model), progress, cts.Token);
            }, Tr.T(model.Name), again);

            if (ok && _main.CurrentImage is { } result)
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
            _ai.HideActivity();
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>Strich aus dem angezeigten Bild ins Pixelbild — Punkte und Radius (beim Entzerren je Stelle verschieden).</summary>
    private BrushStroke ToPixels(BrushStroke s)
    {
        var points = s.Points.Select(p => _main.ShownToPixels(p.X, p.Y)).ToArray();
        var radius = s.Points.Count == 0 ? s.Radius : s.Points.Average(p => _main.ShownLengthToPixels(p.X, p.Y, s.Radius));
        return new BrushStroke(points, radius);
    }

    // ------------------------------------------------------------ Bildwechsel

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
                // anderes Bild: die Markierung gehört zum vorigen
                if (!IsRunning) ClearAll();
                break;
            case nameof(MainViewModel.DocumentSize):
                // Angezeigtes Format geändert (Zuschnitt, Drehen, Werkzeug-Ansicht, Rückgängig davon): die Markierung
                // liegt im angezeigten Bild und passt nicht mehr
                var now = (_main.DocumentSize.Width, _main.DocumentSize.Height);
                if (!IsRunning && _size is { } size && size != now) ClearAll();
                _size = now;
                break;
        }
    }

    private (int W, int H)? _size;
}
