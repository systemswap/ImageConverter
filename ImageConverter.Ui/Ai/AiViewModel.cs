using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImageConverter.Core.Ai;
using ImageConverter.Imaging;
using ImageConverter.ViewModels;

namespace ImageConverter.Ai;

/// <summary>
/// KI-Verbesserung als eigenständiges Modul: GroupBox im Hauptfenster (<see cref="AiPanel"/>)
/// und Tab „KI-Modelle“ in den Einstellungen (<see cref="AiModelsView"/>).
/// Entfernen: Ordner Ai/ (Ui und Core), die zwei Einbindungen in MainView/SettingsWindow,
/// die Eigenschaft <c>MainViewModel.Ai</c> und das Paket Microsoft.ML.OnnxRuntime.
/// </summary>
public partial class AiViewModel : ViewModelBase
{
    private static readonly HttpClient Http = CreateHttp();

    private readonly MainViewModel _main;
    private readonly OnnxSessions _sessions = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private CancellationTokenSource? _runCts;

    public AiViewModel(MainViewModel main)
    {
        _main = main;
        Store = AiModelStore.CreateDefault();
        Models = AiModelCatalog.All.Select(m => new AiModelItem(this, m)).ToList();

        SizeOptions = ["Größe behalten", "Doppelte Größe (2×)", "Vierfache Größe (4×)"];
        SelectedSize = SizeOptions[0];

        Rescan();
        WatchFolders();
        _main.PropertyChanged += OnMainPropertyChanged;
    }

    internal AiModelStore Store { get; }

    /// <summary>Alle Modelle für die Verwaltung (Einstellungen).</summary>
    public IReadOnlyList<AiModelItem> Models { get; }

    /// <summary>Vorhandene, wählbare Modelle (ohne Hilfsmodelle).</summary>
    public ObservableCollection<AiModel> Available { get; } = [];

    /// <summary>Wo die Modelle liegen (bzw. hinkommen).</summary>
    public string FolderText => Store.Folders.FirstOrDefault(Directory.Exists) ?? Store.Folders[0];

    /// <summary>Modellordner im Dateimanager öffnen — z. B. um Modelle von Hand hineinzulegen.</summary>
    [RelayCommand]
    private void OpenFolder()
    {
        var folder = Store.DownloadFolder;   // legt ihn bei Bedarf an
        Directory.CreateDirectory(folder);
        WatchFolders();
        if (!ImageConverter.Services.FolderOpener.TryOpen(folder))
            Status = $"Kein Dateimanager gefunden. Der Ordner ist: {folder}";
        OnPropertyChanged(nameof(FolderText));
    }

    // ------------------------------------------------------------ Auswahl

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEnhanceModel), nameof(ModelHint))]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial AiModel? SelectedModel { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StrengthLabel))]
    public partial double Strength { get; set; } = 100;

    public string StrengthLabel => $"{Strength:0} %";

    public IReadOnlyList<string> SizeOptions { get; }
    [ObservableProperty] public partial string SelectedSize { get; set; }

    public bool IsEnhanceModel => SelectedModel?.Kind == AiModelKind.Enhance;
    public bool HasModels => Available.Count > 0;

    public string? ModelHint => SelectedModel is { } m
        ? m.Description + (m.NonCommercial ? "\n\nLizenz: nur für nicht-kommerzielle Nutzung." : "")
        : null;

    // ------------------------------------------------------------ Lauf

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    /// <summary>Fortschritt 0…100.</summary>
    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }

    private bool CanApply => !IsRunning && SelectedModel is not null && _main.HasImage && !_main.IsEditing;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (SelectedModel is not { } model || _main.CurrentImage is not { } image) return;
        if (Store.PathOf(model) is not { } modelPath)
        {
            Status = "Modell fehlt — bitte in den Einstellungen herunterladen.";
            return;
        }

        var factor = SelectedSize == SizeOptions[2] ? 4 : SelectedSize == SizeOptions[1] ? 2 : 1;
        if (model.Kind == AiModelKind.Enhance
            && !AiEnhancer.PossibleFactors(model.Scale, (int)image.Width, (int)image.Height).Contains(factor))
        {
            var mp = image.Width * factor * image.Height * factor / 1e6;
            Status = $"Zu groß: das Ergebnis hätte {mp:0} Megapixel (höchstens {AiEnhancer.MaxOutputPixels / 1_000_000}).";
            return;
        }

        var strength = Strength / 100;
        var path = _main.CurrentImagePath;
        using var cts = new CancellationTokenSource();
        _runCts = cts;
        IsRunning = true;
        Progress = 0;
        Status = "Modell wird geladen …";
        var clock = Stopwatch.StartNew();

        var progress = new Progress<double>(f =>
        {
            Progress = f * 100;
            Status = f <= 0 ? Status : $"{f * 100:0} %{Remaining(clock.Elapsed, f)}";
            _main.ActivityText = $"KI · {Status}";
        });

        void Preview(AiPreviewFrame frame) => Dispatcher.UIThread.Post(() =>
        {
            // nur zeigen, solange noch dasselbe Bild angezeigt wird
            if (IsRunning && _main.CurrentImagePath == path)
                _main.OverrideBitmap = BitmapOps.FromBgra(frame.Bgra, frame.Width, frame.Height);
        });

        var ok = false;
        try
        {
            if (model.Kind == AiModelKind.Face)
            {
                if (Store.PathOf(AiModelCatalog.FaceDetector) is not { } detectorPath)
                {
                    Status = "Die Gesichtserkennung fehlt — bitte in den Einstellungen herunterladen.";
                    return;
                }

                _main.ActivityText = "KI · Gesichter werden gesucht …";
                var faces = await Task.Run(() =>
                    AiFaces.Detect(image, _sessions.Get(AiModelCatalog.FaceDetector, detectorPath)), cts.Token);
                if (faces.Count == 0)
                {
                    Status = "Kein Gesicht gefunden.";
                    return;
                }

                Status = faces.Count == 1 ? "1 Gesicht gefunden …" : $"{faces.Count} Gesichter gefunden …";
                ok = await _main.ApplyImageEditAsync(img =>
                {
                    var face = new OnnxFaceModel(_sessions.Get(model, modelPath), model);
                    return AiFaces.Restore(img, faces, face, strength, progress, cts.Token, Preview);
                });
            }
            else
            {
                ok = await _main.ApplyImageEditAsync(img =>
                {
                    var tiles = new OnnxTileModel(_sessions.Get(model, modelPath), model);
                    return AiEnhancer.Enhance(img, tiles, factor, strength, progress, cts.Token, Preview);
                });
            }

            Status = ok ? $"Fertig nach {FormatDuration(clock.Elapsed)} — rückgängig mit Strg+Z."
                : cts.IsCancellationRequested ? "Abgebrochen." : "Fehlgeschlagen — siehe Meldung oben.";
        }
        catch (OperationCanceledException)
        {
            Status = "Abgebrochen.";
        }
        catch (Exception ex)
        {
            Status = $"Fehler: {ex.Message}";
        }
        finally
        {
            _runCts = null;
            IsRunning = false;
            _main.OverrideBitmap = null;
            _main.ActivityText = null;
        }
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private void Cancel() => _runCts?.Cancel();

    /// <summary>Öffnet die Einstellungen (Tab „KI-Modelle“).</summary>
    [RelayCommand]
    private Task OpenModels() => _main.OpenSettingsCommand.ExecuteAsync(null);

    private static string Remaining(TimeSpan elapsed, double fraction)
    {
        if (fraction < 0.03 || elapsed.TotalSeconds < 2) return "";
        var left = TimeSpan.FromSeconds(elapsed.TotalSeconds / fraction * (1 - fraction));
        return $"  ·  noch ca. {FormatDuration(left)}";
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalSeconds < 60 ? $"{Math.Max(1, Math.Round(t.TotalSeconds)):0} s" : $"{(int)t.TotalMinutes} min {t.Seconds:00} s";

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.HasImage) or nameof(MainViewModel.IsEditing))
            ApplyCommand.NotifyCanExecuteChanged();

        // anderes Bild gewählt: Zwischenstand gehört nicht dazu
        if (e.PropertyName == nameof(MainViewModel.CurrentFileName) && _main.OverrideBitmap is not null)
            _main.OverrideBitmap = null;
    }

    // ------------------------------------------------------------ Modelle verwalten

    /// <summary>Welche Modelle liegen da? Auch von Hand in den Ordner kopierte werden erkannt.</summary>
    public void Rescan()
    {
        foreach (var item in Models) item.Refresh();

        var detector = Store.IsInstalled(AiModelCatalog.FaceDetector);
        var usable = AiModelCatalog.All
            .Where(m => m.Kind != AiModelKind.Helper && Store.IsInstalled(m)
                        && (m.Kind != AiModelKind.Face || detector))
            .ToList();

        var selected = SelectedModel;
        Available.Clear();
        foreach (var m in usable) Available.Add(m);
        SelectedModel = usable.Contains(selected!) ? selected : usable.FirstOrDefault();
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(FolderText));
    }

    internal async Task DownloadAsync(AiModelItem item)
    {
        // Gesichtsmodelle brauchen die Gesichtserkennung — gleich mitladen
        foreach (var id in item.Model.Requires)
            if (Models.FirstOrDefault(m => m.Model.Id == id) is { IsInstalled: false, IsDownloading: false } required)
                _ = required.DownloadCommand.ExecuteAsync(null);

        await Store.DownloadAsync(item.Model, Http, item.ProgressReporter, item.Token);
        Rescan();
        WatchFolders();   // Ordner gibt es evtl. erst jetzt
    }

    internal void Delete(AiModelItem item)
    {
        _sessions.Dispose();   // geöffnete Modelle freigeben, sonst ist die Datei gesperrt
        Store.Delete(item.Model);
        Rescan();
    }

    internal bool IsBusy => IsRunning;

    private void WatchFolders()
    {
        foreach (var folder in Store.Folders.Where(f => Directory.Exists(f) && _watchers.All(w => w.Path != f)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = true, EnableRaisingEvents = true };
                watcher.Created += OnFolderChanged;
                watcher.Deleted += OnFolderChanged;
                watcher.Renamed += OnFolderChanged;
                _watchers.Add(watcher);
            }
            catch
            {
                // ohne Überwachung: erkannt wird dann beim nächsten Öffnen der Einstellungen
            }
        }
    }

    private void OnFolderChanged(object sender, FileSystemEventArgs e) =>
        Dispatcher.UIThread.Post(Rescan, DispatcherPriority.Background);

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ImageConverter");
        return http;
    }
}

/// <summary>Ein Modell in der Verwaltung: vorhanden?, Download mit Fortschritt, abbrechen, löschen.</summary>
public partial class AiModelItem : ObservableObject
{
    private readonly AiViewModel _owner;
    private CancellationTokenSource? _cts;

    public AiModelItem(AiViewModel owner, AiModel model)
    {
        _owner = owner;
        Model = model;
        ProgressReporter = new Progress<double>(f => Progress = f * 100);
    }

    public AiModel Model { get; }
    internal IProgress<double> ProgressReporter { get; }
    internal CancellationToken Token => _cts?.Token ?? CancellationToken.None;

    public string Info =>
        $"{Model.SizeText}  ·  Lizenz {Model.License}" + (Model.NonCommercial ? " (nur nicht-kommerziell)" : "");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ShowDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(ShowDownload))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand), nameof(DeleteCommand), nameof(CancelCommand))]
    public partial bool IsDownloading { get; set; }

    [ObservableProperty] public partial double Progress { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }

    public bool ShowDownload => !IsInstalled && !IsDownloading;

    public string StateText => IsDownloading ? "wird geladen …" : IsInstalled ? "✓ vorhanden" : "nicht geladen";

    internal void Refresh() => IsInstalled = _owner.Store.IsInstalled(Model);

    private bool CanDownload => !IsInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task Download()
    {
        _cts = new CancellationTokenSource();
        IsDownloading = true;
        Progress = 0;
        Problem = null;
        try
        {
            await _owner.DownloadAsync(this);
        }
        catch (OperationCanceledException)
        {
            Problem = null;
        }
        catch (Exception ex)
        {
            Problem = ex is HttpRequestException ? $"Download fehlgeschlagen: {ex.Message}" : ex.Message;
        }
        finally
        {
            IsDownloading = false;
            _cts.Dispose();
            _cts = null;
            Refresh();
        }
    }

    [RelayCommand(CanExecute = nameof(IsDownloading))]
    private void Cancel() => _cts?.Cancel();

    private bool CanDelete => IsInstalled && !IsDownloading;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (_owner.IsBusy)
        {
            Problem = "Gerade läuft eine KI-Berechnung — danach löschen.";
            return;
        }

        try { _owner.Delete(this); }
        catch (Exception ex) { Problem = $"Löschen fehlgeschlagen: {ex.Message}"; }
    }
}
